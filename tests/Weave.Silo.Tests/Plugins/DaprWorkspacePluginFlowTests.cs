using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Plugins;

public sealed class DaprWorkspacePluginFlowTests : IClassFixture<SiloFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SiloFactory _factory;

    public DaprWorkspacePluginFlowTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task StartWorkspace_MixedPluginManifest_RequiresBothInstallGrants()
    {
        using var client = _factory.CreateClient();
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "mixed-plugin-authority",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["sidecar"] = new() { Type = "dapr_tools" },
                ["peer"] = new() { Type = "mcp_tools" }
            }
        };
        using (var daprOnly = await SendWithTokenAsync(client, HttpMethod.Post, "/api/workspaces",
            Mint(_factory.Services, "silo", "workspace:create", "plugin:dapr_tools:install"),
            new { Manifest = manifest }))
            daprOnly.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var mcpOnly = await SendWithTokenAsync(client, HttpMethod.Post, "/api/workspaces",
            Mint(_factory.Services, "silo", "workspace:create", "plugin:mcp_tools:install"),
            new { Manifest = manifest }))
            mcpOnly.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StartWorkspace_DaprManifest_RequiresCreateAndInstallGrants()
    {
        using var client = _factory.CreateClient();
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "dapr-authority",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["sidecar"] = new() { Type = "dapr_tools", Config = new Dictionary<string, string> { ["port"] = "3500" } }
            }
        };

        using (var missing = await client.PostAsJsonAsync("/api/workspaces", new { Manifest = manifest },
            TestContext.Current.CancellationToken))
            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var wrongWorkspace = await SendWithTokenAsync(client, HttpMethod.Post, "/api/workspaces",
            Mint(_factory.Services, "other", "workspace:create", "plugin:dapr_tools:install"),
            new { Manifest = manifest }))
            wrongWorkspace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var missingInstall = await SendWithTokenAsync(client, HttpMethod.Post, "/api/workspaces",
            Mint(_factory.Services, "silo", "workspace:create"), new { Manifest = manifest }))
            missingInstall.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var missingCreate = await SendWithTokenAsync(client, HttpMethod.Post, "/api/workspaces",
            Mint(_factory.Services, "silo", "plugin:dapr_tools:install"), new { Manifest = manifest }))
            missingCreate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var revoked = Mint(_factory.Services, "silo", "workspace:create", "plugin:dapr_tools:install");
        _factory.Services.GetRequiredService<ICapabilityTokenService>().Revoke(revoked.TokenId);
        using (var revokedResponse = await SendWithTokenAsync(client, HttpMethod.Post, "/api/workspaces",
            revoked, new { Manifest = manifest }))
            revokedResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var expired = await SendWithTokenAsync(client, HttpMethod.Post, "/api/workspaces",
            Mint(_factory.Services, "silo", "workspace:create", "plugin:dapr_tools:install",
                TimeSpan.FromSeconds(-1)), new { Manifest = manifest }))
            expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StopWorkspace_DaprInstallation_RequiresStopAndDisableGrants()
    {
        await using var sidecar = CreateSidecar("stop-authority");
        await sidecar.StartAsync(TestContext.Current.CancellationToken);
        using var client = _factory.CreateClient();
        var workspaceId = await StartWorkspaceAsync(client, _factory.Services, sidecar);
        using (var missing = await client.DeleteAsync($"/api/workspaces/{workspaceId}",
            TestContext.Current.CancellationToken))
            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var wrongWorkspace = await SendWithTokenAsync(client, HttpMethod.Delete,
            $"/api/workspaces/{workspaceId}", Mint(_factory.Services, "other", "workspace:stop",
                "plugin:dapr_tools:disable")))
            wrongWorkspace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var missingStopGrant = await SendWithTokenAsync(client, HttpMethod.Delete,
            $"/api/workspaces/{workspaceId}", Mint(_factory.Services, workspaceId, "plugin:dapr_tools:disable")))
            missingStopGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var missingDaprGrant = await SendWithTokenAsync(client, HttpMethod.Delete,
            $"/api/workspaces/{workspaceId}", Mint(_factory.Services, workspaceId, "workspace:stop")))
            missingDaprGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var actors = _factory.Services.GetRequiredService<IVirtualActorProvider>();
        var workspace = actors.GetActor<Weave.Workspaces.Lifecycle.IWorkspaceActor>(VirtualActorId.From(workspaceId));
        (await workspace.GetStateAsync()).DaprToolInstallations.Single().DesiredEnabled.ShouldBeTrue();
        using var stop = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/workspaces/{workspaceId}", workspaceId, "workspace:stop", "plugin:dapr_tools:disable");
        stop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DisconnectPlugin_DaprInstallation_RequiresDisableGrant()
    {
        await using var sidecar = CreateSidecar("disable-authority");
        await sidecar.StartAsync(TestContext.Current.CancellationToken);
        using var client = _factory.CreateClient();
        var workspaceId = await StartWorkspaceAsync(client, _factory.Services, sidecar);
        using (var missing = await client.DeleteAsync($"/api/plugins/{workspaceId}/sidecar",
            TestContext.Current.CancellationToken))
            missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var wrongWorkspace = await SendWithTokenAsync(client, HttpMethod.Delete,
            $"/api/plugins/{workspaceId}/sidecar", Mint(_factory.Services, "other", "plugin:dapr_tools:disable")))
            wrongWorkspace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var actors = _factory.Services.GetRequiredService<IVirtualActorProvider>();
        var workspace = actors.GetActor<Weave.Workspaces.Lifecycle.IWorkspaceActor>(VirtualActorId.From(workspaceId));
        (await workspace.GetStateAsync()).DaprToolInstallations.Single().DesiredEnabled.ShouldBeTrue();
        using (var disable = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/plugins/{workspaceId}/sidecar", workspaceId, "plugin:dapr_tools:disable"))
            disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.GetStateAsync()).DaprToolInstallations.Single().DesiredEnabled.ShouldBeFalse();
        using var stop = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/workspaces/{workspaceId}", workspaceId, "workspace:stop", "plugin:dapr_tools:disable");
        stop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ConnectPlugin_DisabledDaprInstallation_RequiresEnableGrant()
    {
        await using var sidecar = CreateSidecar("enable-authority");
        await sidecar.StartAsync(TestContext.Current.CancellationToken);
        using var client = _factory.CreateClient();
        var workspaceId = await StartWorkspaceAsync(client, _factory.Services, sidecar);
        using (var disable = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/plugins/{workspaceId}/sidecar", workspaceId, "plugin:dapr_tools:disable"))
            disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var actors = _factory.Services.GetRequiredService<IVirtualActorProvider>();
        var workspace = actors.GetActor<Weave.Workspaces.Lifecycle.IWorkspaceActor>(VirtualActorId.From(workspaceId));
        var port = (await workspace.GetStateAsync()).DaprToolInstallations.Single().Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var enableRequest = new
        {
            Name = $"{workspaceId}/sidecar",
            Type = "dapr_tools",
            Config = new Dictionary<string, string> { ["port"] = port }
        };
        using (var missingEnable = await client.PostAsJsonAsync("/api/plugins", enableRequest,
            TestContext.Current.CancellationToken))
            missingEnable.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var wrongEnable = await SendWithTokenAsync(client, HttpMethod.Post, "/api/plugins",
            Mint(_factory.Services, "other", "plugin:dapr_tools:enable"), enableRequest))
            wrongEnable.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await workspace.GetStateAsync()).DaprToolInstallations.Single().DesiredEnabled.ShouldBeFalse();
        using (var enable = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Post,
            "/api/plugins", workspaceId, "plugin:dapr_tools:enable", null, enableRequest))
            enable.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var stop = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/workspaces/{workspaceId}", workspaceId, "workspace:stop", "plugin:dapr_tools:disable");
        stop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Restart_PersistedDaprInstallation_ReactivatesUntilDisabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"weave-dapr-installation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var sidecar = CreateSidecar("recovered");
            await sidecar.StartAsync(TestContext.Current.CancellationToken);
            string workspaceId;
            await using (var first = new DurableSiloFactory(directory))
            {
                using var client = first.CreateClient();
                workspaceId = await StartWorkspaceAsync(client, first.Services, sidecar);
            }

            await using (var second = new DurableSiloFactory(directory))
            {
                using var client = second.CreateClient();
                await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                using var composition = await client.GetAsync("/api/plugins/composition", TestContext.Current.CancellationToken);
                (await composition.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                    .ShouldContain($"{workspaceId}/sidecar");

                var actors = second.Services.GetRequiredService<IVirtualActorProvider>();
                var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
                var resolution = await registry.ResolveAsync("caller", "echo");
                resolution.ShouldNotBeNull();
                var tool = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo"));
                var result = await tool.InvokeAsync(new ToolInvocation
                {
                    ToolName = "echo",
                    Method = "ping",
                    InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
                }, resolution.Token);
                result.Success.ShouldBeTrue(result.Error);
                result.Output.ShouldContain("recovered");

                using var disable = await SendAuthorizedAsync(client, second.Services, HttpMethod.Delete,
                    $"/api/plugins/{workspaceId}/sidecar", workspaceId, "plugin:dapr_tools:disable");
                disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }

            await using (var third = new DurableSiloFactory(directory))
            {
                using var client = third.CreateClient();
                await third.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                using var composition = await client.GetAsync("/api/plugins/composition", TestContext.Current.CancellationToken);
                (await composition.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                    .ShouldNotContain($"{workspaceId}/sidecar");
                var actors = third.Services.GetRequiredService<IVirtualActorProvider>();
                var workspace = actors.GetActor<Weave.Workspaces.Lifecycle.IWorkspaceActor>(VirtualActorId.From(workspaceId));
                (await workspace.GetStateAsync()).DaprToolInstallations.Single().DesiredEnabled.ShouldBeFalse();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class DurableSiloFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Weave:LocalMode", "true");
            builder.UseSetting("Weave:ActorStorage:Provider", "sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(directory, "actors.db")};Pooling=False");
            builder.UseSetting("Weave:Invocations:DatabasePath", Path.Combine(directory, "invocations.db"));
            builder.UseSetting("urls", "http://127.0.0.1:0");
            builder.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }));
        }
    }

    [Fact]
    public async Task TwoWorkspaces_DistinctDaprInstallations_RouteToTheirOwnSidecars()
    {
        await using var firstSidecar = CreateSidecar("first");
        await using var secondSidecar = CreateSidecar("second");
        await firstSidecar.StartAsync(TestContext.Current.CancellationToken);
        await secondSidecar.StartAsync(TestContext.Current.CancellationToken);

        using var client = _factory.CreateClient();
        var firstId = await StartWorkspaceAsync(client, _factory.Services, firstSidecar);
        var secondId = await StartWorkspaceAsync(client, _factory.Services, secondSidecar);
        var actors = _factory.Services.GetRequiredService<IVirtualActorProvider>();

        foreach (var (workspaceId, expected) in new[] { (firstId, "first"), (secondId, "second") })
        {
            var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
            var resolution = await registry.ResolveAsync("caller", "echo");
            resolution.ShouldNotBeNull();
            var tool = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo"));
            var result = await tool.InvokeAsync(new ToolInvocation
            {
                ToolName = "echo",
                Method = "ping",
                InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
            }, resolution.Token);
            result.Success.ShouldBeTrue(result.Error);
            result.Output.ShouldContain(expected);
        }

        using var firstStop = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/workspaces/{firstId}", firstId, "workspace:stop", "plugin:dapr_tools:disable");
        firstStop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var remainingRegistry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(secondId));
        var remainingResolution = await remainingRegistry.ResolveAsync("caller", "echo");
        remainingResolution.ShouldNotBeNull();
        var remainingTool = actors.GetActor<IToolActor>(VirtualActorId.From($"{secondId}/echo"));
        var remainingResult = await remainingTool.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, remainingResolution.Token);
        remainingResult.Success.ShouldBeTrue(remainingResult.Error);
        remainingResult.Output.ShouldContain("second");
        using var secondStop = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/workspaces/{secondId}", secondId, "workspace:stop", "plugin:dapr_tools:disable");
        secondStop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static WebApplication CreateSidecar(string source)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var sidecar = builder.Build();
        sidecar.MapPost("/v1.0/invoke/echo-service/method/ping", () => Results.Json(new { source }));
        return sidecar;
    }

    private static async Task<string> StartWorkspaceAsync(HttpClient client, IServiceProvider services, WebApplication sidecar)
    {
        var address = sidecar.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = $"dapr-tool-{Guid.NewGuid():N}",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["sidecar"] = new()
                {
                    Type = "dapr_tools",
                    Config = new Dictionary<string, string> { ["port"] = new Uri(address).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                }
            },
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["echo"] = new() { Type = "dapr", RequiresPlugin = "sidecar", Dapr = new DaprToolConfig { AppId = "echo-service" } }
            },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["caller"] = new() { Model = "test", Tools = ["echo"], Capabilities = ["tool:echo:invoke:ping"] }
            }
        };
        using var response = await SendAuthorizedAsync(client, services, HttpMethod.Post,
            "/api/workspaces", "silo", "workspace:create", "plugin:dapr_tools:install",
            new { Manifest = manifest });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("workspaceId").GetString()!;
    }

    [Fact]
    public async Task Workspace_DaprToolDependency_ActivatesOnlyWithExplicitInvocationGrantAndStops()
    {
        var sidecarBuilder = WebApplication.CreateBuilder();
        sidecarBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var sidecar = sidecarBuilder.Build();
        sidecar.MapPost("/v1.0/invoke/echo-service/method/ping", () => Results.Json(new { pong = true }));
        await sidecar.StartAsync(TestContext.Current.CancellationToken);
        var address = sidecar.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var port = new Uri(address).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var client = _factory.CreateClient();
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = $"dapr-tool-{Guid.NewGuid():N}",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["sidecar"] = new()
                {
                    Type = "dapr_tools",
                    Config = new Dictionary<string, string> { ["port"] = port }
                }
            },
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["echo"] = new()
                {
                    Type = "dapr",
                    RequiresPlugin = "sidecar",
                    Dapr = new DaprToolConfig { AppId = "echo-service" }
                }
            },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["observer"] = new() { Model = "test", Tools = ["echo"] },
                ["caller"] = new()
                {
                    Model = "test",
                    Tools = ["echo"],
                    Capabilities = ["tool:echo:invoke:ping"]
                }
            }
        };

        using var start = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Post,
            "/api/workspaces", "silo", "workspace:create", "plugin:dapr_tools:install",
            new { Manifest = manifest });
        var body = await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var workspaceId = document.RootElement.GetProperty("workspaceId").GetString()!;

        var actors = _factory.Services.GetRequiredService<IVirtualActorProvider>();
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
        (await registry.GetConnectionAsync("echo"))!.Status.ShouldBe(ToolConnectionStatus.Connected);
        (await registry.ResolveAsync("observer", "echo")).ShouldBeNull();
        var authorized = await registry.ResolveAsync("caller", "echo");
        authorized.ShouldNotBeNull();
        authorized.Token.Grants.ShouldContain("tool:echo:invoke:ping");
        var actor = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo"));
        (await actor.GetHandleAsync())!.ConnectionId.ShouldBe("dapr:echo-service");
        var result = await actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token);
        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldContain("pong");
        result.OutcomeRecorded.ShouldBeTrue();

        using var composition = await client.GetAsync("/api/plugins/composition",
            TestContext.Current.CancellationToken);
        var active = await composition.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        active.ShouldContain($"{workspaceId}/sidecar");

        using var disable = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/plugins/{workspaceId}/sidecar", workspaceId, "plugin:dapr_tools:disable");
        disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));

        using var changedConfig = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Post,
            "/api/plugins", workspaceId, "plugin:dapr_tools:enable", null, new
            {
                Name = $"{workspaceId}/sidecar",
                Type = "dapr_tools",
                Config = new Dictionary<string, string> { ["port"] = port == "3500" ? "3501" : "3500" }
            });
        changedConfig.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var reactivate = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Post,
            "/api/plugins", workspaceId, "plugin:dapr_tools:enable", null, new
            {
                Name = $"{workspaceId}/sidecar",
                Type = "dapr_tools",
                Config = new Dictionary<string, string> { ["port"] = port }
            });
        reactivate.StatusCode.ShouldBe(HttpStatusCode.Created);
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));

        using var stop = await SendAuthorizedAsync(client, _factory.Services, HttpMethod.Delete,
            $"/api/workspaces/{workspaceId}", workspaceId, "workspace:stop", "plugin:dapr_tools:disable");
        stop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await actor.GetHandleAsync()).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));
        using var after = await client.GetAsync("/api/plugins/composition",
            TestContext.Current.CancellationToken);
        (await after.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain($"{workspaceId}/sidecar");
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(HttpClient client, IServiceProvider services,
        HttpMethod method, string path, string workspaceId, string grant, string? secondGrant = null, object? body = null)
    {
        return await SendWithTokenAsync(client, method, path, Mint(services, workspaceId, grant, secondGrant), body);
    }

    private static CapabilityToken Mint(IServiceProvider services, string workspaceId, string grant,
        string? secondGrant = null, TimeSpan? lifetime = null) =>
        services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "dapr-installation-operator",
            Grants = secondGrant is null ? [grant] : [grant, secondGrant],
            Lifetime = lifetime ?? TimeSpan.FromMinutes(5)
        });

    private static async Task<HttpResponseMessage> SendWithTokenAsync(HttpClient client, HttpMethod method,
        string path, CapabilityToken token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Weave-Capability",
            WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions)));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
