using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
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
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Plugins;

public sealed class DaprWorkspacePluginFlowTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public DaprWorkspacePluginFlowTests(SiloFactory factory) => _factory = factory;

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
                workspaceId = await StartWorkspaceAsync(client, sidecar);
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

                using var disable = await client.DeleteAsync($"/api/plugins/{workspaceId}/sidecar",
                    TestContext.Current.CancellationToken);
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
        var firstId = await StartWorkspaceAsync(client, firstSidecar);
        var secondId = await StartWorkspaceAsync(client, secondSidecar);
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

        using var firstStop = await client.DeleteAsync($"/api/workspaces/{firstId}", TestContext.Current.CancellationToken);
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
        using var secondStop = await client.DeleteAsync($"/api/workspaces/{secondId}", TestContext.Current.CancellationToken);
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

    private static async Task<string> StartWorkspaceAsync(HttpClient client, WebApplication sidecar)
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
        using var response = await client.PostAsJsonAsync("/api/workspaces", new { Manifest = manifest }, TestContext.Current.CancellationToken);
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

        using var start = await client.PostAsJsonAsync("/api/workspaces",
            new { Manifest = manifest }, TestContext.Current.CancellationToken);
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

        using var disable = await client.DeleteAsync($"/api/plugins/{workspaceId}/sidecar",
            TestContext.Current.CancellationToken);
        disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));

        using var changedConfig = await client.PostAsJsonAsync("/api/plugins", new
        {
            Name = $"{workspaceId}/sidecar",
            Type = "dapr_tools",
            Config = new Dictionary<string, string> { ["port"] = port == "3500" ? "3501" : "3500" }
        }, TestContext.Current.CancellationToken);
        changedConfig.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var reactivate = await client.PostAsJsonAsync("/api/plugins", new
        {
            Name = $"{workspaceId}/sidecar",
            Type = "dapr_tools",
            Config = new Dictionary<string, string> { ["port"] = port }
        }, TestContext.Current.CancellationToken);
        reactivate.StatusCode.ShouldBe(HttpStatusCode.Created);
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));

        using var stop = await client.DeleteAsync($"/api/workspaces/{workspaceId}",
            TestContext.Current.CancellationToken);
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
}
