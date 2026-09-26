using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Weave.Agents.ToolRegistry;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Plugins;

public sealed class McpWorkspacePluginFlowTests
{
    private const string EchoServerVersion = "0.1.1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StartWorkspaceAsync_McpManifest_RequiresCreatorAndInstallCapability()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("authority");
            await peer.StartAsync();
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            var manifest = CreateManifest(peer.Endpoint);
            using (var response = await SendStartAsync(client, manifest))
                response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using (var wrongWorkspace = await SendStartAsync(client, manifest,
                Mint(host.Services, "other", "workspace:create", secondGrant: "plugin:mcp_tools:install")))
                wrongWorkspace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using (var missingGrant = await SendStartAsync(client, manifest,
                Mint(host.Services, "silo", "workspace:create")))
                missingGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using (var missingCreate = await SendStartAsync(client, manifest,
                Mint(host.Services, "silo", "plugin:mcp_tools:install")))
                missingCreate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var revoked = Mint(host.Services, "silo", "workspace:create",
                secondGrant: "plugin:mcp_tools:install");
            host.Services.GetRequiredService<ICapabilityTokenService>().Revoke(revoked.TokenId);
            using (var revokedResponse = await SendStartAsync(client, manifest, revoked))
                revokedResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using (var expired = await SendStartAsync(client, manifest,
                Mint(host.Services, "silo", "workspace:create", TimeSpan.FromSeconds(-1),
                    "plugin:mcp_tools:install")))
                expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            peer.CallCount.ShouldBe(0);
            var workspaceId = await StartWorkspaceAsync(client, host.Services, peer);
            workspaceId.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopWorkspaceAsync_McpInstallation_RequiresWorkspaceDisableCapability()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-stop-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("stop-authority");
            await peer.StartAsync();
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            var workspaceId = await StartWorkspaceAsync(client, host.Services, peer);

            using var denied = await client.DeleteAsync($"/api/workspaces/{workspaceId}",
                TestContext.Current.CancellationToken);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using (var wrongWorkspace = await SendPluginAsync(client, host.Services, HttpMethod.Delete,
                $"/api/workspaces/{workspaceId}", "other", "workspace:stop", token:
                    Mint(host.Services, "other", "workspace:stop", secondGrant: "plugin:mcp_tools:disable")))
                wrongWorkspace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using (var missingDisable = await SendPluginAsync(client, host.Services, HttpMethod.Delete,
                $"/api/workspaces/{workspaceId}", workspaceId, "workspace:stop"))
                missingDisable.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using (var missingStop = await SendPluginAsync(client, host.Services, HttpMethod.Delete,
                $"/api/workspaces/{workspaceId}", workspaceId, "plugin:mcp_tools:disable"))
                missingStop.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            await InvokeAsync(host.Services, workspaceId, "stop-authority");
            using var allowed = await SendPluginAsync(client, host.Services, HttpMethod.Delete,
                $"/api/workspaces/{workspaceId}", workspaceId, "workspace:stop", token:
                    Mint(host.Services, workspaceId, "workspace:stop", secondGrant: "plugin:mcp_tools:disable"));
            allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            var workspace = host.Services.GetRequiredService<IVirtualActorProvider>()
                .GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
            (await workspace.GetStateAsync()).McpToolInstallations.Single().DesiredEnabled.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_TwoMcpInstallations_RouteIndependentlyAndHonorDisable()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-installation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var firstPeer = new McpPeer("first");
            await using var secondPeer = new McpPeer("second");
            await firstPeer.StartAsync();
            await secondPeer.StartAsync();
            string firstId;
            string secondId;
            await using (var first = new DurableSiloFactory(directory))
            {
                using var client = first.CreateClient();
                firstId = await StartWorkspaceAsync(client, first.Services, firstPeer);
                secondId = await StartWorkspaceAsync(client, first.Services, secondPeer);
                await InvokeAsync(first.Services, firstId, "first");
                await InvokeAsync(first.Services, secondId, "second");
                var actors = first.Services.GetRequiredService<IVirtualActorProvider>();
                var tool = actors.GetActor<IToolActor>(VirtualActorId.From($"{firstId}/echo"));
                var handle = await tool.GetHandleAsync();
                handle.ShouldNotBeNull();
                var connector = first.Services.GetRequiredService<IToolDiscoveryService>()
                    .GetConnector(ToolType.Mcp, $"{firstId}/echo_server");
                var binding = connector.ShouldBeAssignableTo<IApprovalTargetBinding>();
                binding.GetApprovalTargetDigest(handle).ShouldNotBeNullOrWhiteSpace();
                var description = binding.GetApprovalTargetDescription(handle);
                description.ShouldNotBeNull();
                description.ShouldContain(firstPeer.Endpoint);
                var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(firstId));
                var conflicting = new WorkspaceManifest
                {
                    Version = "1.0",
                    Name = "conflicting-installation",
                    Plugins = new Dictionary<string, PluginDefinition>
                    {
                        ["echo_server"] = new()
                        {
                            Type = "dapr_tools",
                            Config = new Dictionary<string, string> { ["port"] = "3500" }
                        }
                    },
                    Tools = new Dictionary<string, ToolDefinition>
                    {
                        ["echo"] = new()
                        {
                            Type = "dapr",
                            RequiresPlugin = "echo_server",
                            Dapr = new DaprToolConfig { AppId = "other-service" }
                        }
                    }
                };
                await Should.ThrowAsync<InvalidOperationException>(() => workspace.StartAsync(conflicting));
            }

            await using (var second = new DurableSiloFactory(directory))
            {
                using var client = second.CreateClient();
                await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                await InvokeAsync(second.Services, firstId, "first");
                await InvokeAsync(second.Services, secondId, "second");
                using (var missing = await client.DeleteAsync($"/api/plugins/{firstId}/echo_server",
                    TestContext.Current.CancellationToken))
                    missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                using (var crossWorkspace = await SendPluginAsync(client, second.Services, HttpMethod.Delete,
                    $"/api/plugins/{firstId}/echo_server", secondId, "plugin:mcp_tools:disable"))
                    crossWorkspace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                var revoked = Mint(second.Services, firstId, "plugin:mcp_tools:disable");
                second.Services.GetRequiredService<ICapabilityTokenService>().Revoke(revoked.TokenId);
                using (var revokedResponse = await SendPluginAsync(client, second.Services, HttpMethod.Delete,
                    $"/api/plugins/{firstId}/echo_server", firstId, "plugin:mcp_tools:disable", token: revoked))
                    revokedResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                var firstWorkspace = second.Services.GetRequiredService<IVirtualActorProvider>()
                    .GetActor<IWorkspaceActor>(VirtualActorId.From(firstId));
                (await firstWorkspace.GetStateAsync()).McpToolInstallations.Single().DesiredEnabled.ShouldBeTrue();
                using var disable = await SendPluginAsync(client, second.Services, HttpMethod.Delete,
                    $"/api/plugins/{firstId.ToUpperInvariant()}/ECHO_SERVER", firstId,
                    "plugin:mcp_tools:disable");
                disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                var actors = second.Services.GetRequiredService<IVirtualActorProvider>();
                var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(firstId));
                (await workspace.GetStateAsync()).McpToolInstallations.Single().DesiredEnabled.ShouldBeFalse();
                var (disabledTool, disabledToken) = await ResolveAsync(second.Services, firstId);
                await Should.ThrowAsync<InvalidOperationException>(() => disabledTool.InvokeAsync(new ToolInvocation
                {
                    ToolName = "echo",
                    Method = "echo",
                    InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
                    Parameters = new Dictionary<string, string> { ["text"] = "blocked" }
                }, disabledToken));
                using (var missingEnable = await client.PostAsJsonAsync("/api/plugins", new
                {
                    Name = $"{firstId}/echo_server",
                    Type = "mcp_tools"
                }, TestContext.Current.CancellationToken))
                    missingEnable.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                using (var wrongEnableGrant = await SendPluginAsync(client, second.Services, HttpMethod.Post,
                    "/api/plugins", firstId, "plugin:mcp_tools:disable", new
                    {
                        Name = $"{firstId}/echo_server",
                        Type = "mcp_tools"
                    }))
                    wrongEnableGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                using (var expiredEnable = await SendPluginAsync(client, second.Services, HttpMethod.Post,
                    "/api/plugins", firstId, "plugin:mcp_tools:enable", new
                    {
                        Name = $"{firstId}/echo_server",
                        Type = "mcp_tools"
                    }, Mint(second.Services, firstId, "plugin:mcp_tools:enable", TimeSpan.FromSeconds(-1))))
                    expiredEnable.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                (await workspace.GetStateAsync()).McpToolInstallations.Single().DesiredEnabled.ShouldBeFalse();
                using var enable = await SendPluginAsync(client, second.Services, HttpMethod.Post,
                    "/api/plugins", firstId, "plugin:mcp_tools:enable", new
                    {
                        Name = $"{firstId}/ECHO_SERVER",
                        Type = "mcp_tools"
                    });
                enable.StatusCode.ShouldBe(HttpStatusCode.Created);
                await InvokeAsync(second.Services, firstId, "first");
                using var disableAgain = await SendPluginAsync(client, second.Services, HttpMethod.Delete,
                    $"/api/plugins/{firstId}/ECHO_SERVER", firstId, "plugin:mcp_tools:disable");
                disableAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                await InvokeAsync(second.Services, secondId, "second");
            }

            await using (var third = new DurableSiloFactory(directory))
            {
                using var client = third.CreateClient();
                await third.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                using var composition = await client.GetAsync("/api/plugins/composition",
                    TestContext.Current.CancellationToken);
                var active = await composition.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                active.ShouldNotContain($"{firstId}/echo_server");
                active.ShouldContain($"{secondId}/echo_server");
                await InvokeAsync(third.Services, secondId, "second");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_ChangedMcpSchema_BlocksExternalCall()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("original");
            await peer.StartAsync();
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            var workspaceId = await StartWorkspaceAsync(client, host.Services, peer);
            await InvokeAsync(host.Services, workspaceId, "original");
            var calls = peer.CallCount;
            peer.AnnotationTitle = "changed contract";
            var (tool, token) = await ResolveAsync(host.Services, workspaceId);
            var result = await tool.InvokeAsync(new ToolInvocation
            {
                ToolName = "echo",
                Method = "echo",
                InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
                Parameters = new Dictionary<string, string> { ["text"] = "blocked" }
            }, token);
            result.Success.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.ShouldContain("contract");
            peer.CallCount.ShouldBe(calls);
            peer.AnnotationTitle = "Echo";
            peer.ServerVersion = "0.2.0";
            var changedVersion = await tool.InvokeAsync(new ToolInvocation
            {
                ToolName = "echo",
                Method = "echo",
                InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
                Parameters = new Dictionary<string, string> { ["text"] = "blocked again" }
            }, token);
            changedVersion.Success.ShouldBeFalse();
            changedVersion.Error.ShouldNotBeNull();
            changedVersion.Error.ShouldContain("identity");
            peer.CallCount.ShouldBe(calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReviewApprovalAsync_DisabledMcpInstallation_ReturnsUnavailable()
    {
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var peer = new McpPeer("review");
            await peer.StartAsync();
            await using var host = new DurableSiloFactory(directory, requireApproval: true);
            using var client = host.CreateClient();
            var workspaceId = await StartWorkspaceAsync(client, host.Services, peer);
            var (tool, writer) = await ResolveAsync(host.Services, workspaceId);
            var request = new ToolInvocation
            {
                ToolName = "echo",
                Method = "echo",
                InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
                Parameters = new Dictionary<string, string> { ["text"] = "not dispatched" }
            };
            (await tool.InvokeAsync(request, writer)).ErrorCode.ShouldBe("approval-pending");
            peer.CallCount.ShouldBe(0);
            var reviewer = host.Services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
            {
                WorkspaceId = workspaceId,
                IssuedTo = "reviewer",
                Grants = ["invocation:read", "approval:decide", "tool:echo:approve:echo"],
                Lifetime = TimeSpan.FromMinutes(5)
            });
            (await tool.ReviewApprovalAsync(request, reviewer)).Review.ShouldNotBeNull();

            using var disable = await SendPluginAsync(client, host.Services, HttpMethod.Delete,
                $"/api/plugins/{workspaceId}/ECHO_SERVER", workspaceId, "plugin:mcp_tools:disable");
            disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var review = await tool.ReviewApprovalAsync(request, reviewer);
            review.Review.ShouldBeNull();
            review.ErrorCode.ShouldBe("approval-review-unavailable");
            peer.CallCount.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EchoPackage_HttpInstallation_InvokesThroughWeave()
    {
        var endpoint = Environment.GetEnvironmentVariable("WEAVE_ECHO_TEST_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
            Assert.Skip("Set WEAVE_ECHO_TEST_ENDPOINT to the running Echo package HTTP endpoint.");
        var directory = Path.Join(Path.GetTempPath(), $"weave-mcp-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string firstId;
            string secondId;
            await using (var first = new DurableSiloFactory(directory))
            {
                using var client = first.CreateClient();
                firstId = await StartWorkspaceAsync(client, first.Services, endpoint);
                secondId = await StartWorkspaceAsync(client, first.Services, endpoint);
                await InvokeAsync(first.Services, firstId, "first package response");
                await InvokeAsync(first.Services, secondId, "second package response");
            }

            await using (var second = new DurableSiloFactory(directory))
            {
                using var client = second.CreateClient();
                await second.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                await InvokeAsync(second.Services, firstId, "first after restart");
                await InvokeAsync(second.Services, secondId, "second after restart");
                using var disable = await SendPluginAsync(client, second.Services, HttpMethod.Delete,
                    $"/api/plugins/{firstId}/ECHO_SERVER", firstId, "plugin:mcp_tools:disable");
                disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                var actors = second.Services.GetRequiredService<IVirtualActorProvider>();
                var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(firstId));
                (await workspace.GetStateAsync()).McpToolInstallations.Single().DesiredEnabled.ShouldBeFalse();
                var (disabledTool, disabledToken) = await ResolveAsync(second.Services, firstId);
                await Should.ThrowAsync<InvalidOperationException>(() => disabledTool.InvokeAsync(new ToolInvocation
                {
                    ToolName = "echo",
                    Method = "echo",
                    InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
                    Parameters = new Dictionary<string, string> { ["text"] = "blocked" }
                }, disabledToken));
                await InvokeAsync(second.Services, secondId, "other workspace remains active");
            }

            await using (var third = new DurableSiloFactory(directory))
            {
                using var client = third.CreateClient();
                await third.Services.GetRequiredService<ToolInstallationRestorer>().Completion
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                using var composition = await client.GetAsync("/api/plugins/composition",
                    TestContext.Current.CancellationToken);
                var active = await composition.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                active.ShouldNotContain($"{firstId}/echo_server");
                active.ShouldContain($"{secondId}/echo_server");
                await InvokeAsync(third.Services, secondId, "second package response after restart");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task InvokeAsync(IServiceProvider services, string workspaceId, string expected)
    {
        var actors = services.GetRequiredService<IVirtualActorProvider>();
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
        (await registry.ResolveAsync("observer", "echo")).ShouldBeNull();
        var (tool, token) = await ResolveAsync(services, workspaceId);
        var result = await tool.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "echo",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
            Parameters = new Dictionary<string, string> { ["text"] = expected }
        }, token);
        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldBe(expected);
    }

    private static async Task<(IToolActor Tool, Weave.Security.Tokens.CapabilityToken Token)> ResolveAsync(
        IServiceProvider services, string workspaceId)
    {
        var actors = services.GetRequiredService<IVirtualActorProvider>();
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
        var resolution = await registry.ResolveAsync("caller", "echo");
        resolution.ShouldNotBeNull();
        return (actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo")), resolution.Token);
    }

    private static Task<string> StartWorkspaceAsync(HttpClient client, IServiceProvider services, McpPeer peer) =>
        StartWorkspaceAsync(client, services, peer.Endpoint);

    private static async Task<string> StartWorkspaceAsync(HttpClient client, IServiceProvider services, string endpoint)
    {
        var manifest = CreateManifest(endpoint);
        using var response = await SendStartAsync(client, manifest,
            Mint(services, "silo", "workspace:create", secondGrant: "plugin:mcp_tools:install"));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("workspaceId").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendStartAsync(HttpClient client, WorkspaceManifest manifest,
        CapabilityToken? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspaces")
        {
            Content = JsonContent.Create(new { Manifest = manifest })
        };
        if (token is not null)
            request.Headers.Add("X-Weave-Capability", Encode(token));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> SendPluginAsync(HttpClient client, IServiceProvider services,
        HttpMethod method, string path, string workspaceId, string grant, object? body = null,
        CapabilityToken? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add("X-Weave-Capability", Encode(token ?? Mint(services, workspaceId, grant)));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static CapabilityToken Mint(IServiceProvider services, string workspaceId, string grant,
        TimeSpan? lifetime = null, string? secondGrant = null) =>
        services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "mcp-installation-operator",
            Grants = secondGrant is null ? [grant] : [grant, secondGrant],
            Lifetime = lifetime ?? TimeSpan.FromMinutes(5)
        });

    private static string Encode(CapabilityToken token) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions));

    private static WorkspaceManifest CreateManifest(string endpoint) => new()
    {
        Version = "1.0",
        Name = $"mcp-tool-{Guid.NewGuid():N}",
        Plugins = new Dictionary<string, PluginDefinition>
        {
            ["echo_server"] = new()
            {
                Type = "mcp_tools",
                Config = new Dictionary<string, string>
                {
                    ["server_name"] = "weave-plugin-echo",
                    ["server_version"] = EchoServerVersion,
                    ["operation"] = "echo"
                }
            }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["echo"] = new()
            {
                Type = "mcp",
                RequiresPlugin = "echo_server",
                Mcp = new McpConfig { Url = endpoint, AllowPrivateEndpoints = true }
            }
        },
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["observer"] = new() { Model = "test", Tools = ["echo"] },
            ["caller"] = new()
            {
                Model = "test",
                Tools = ["echo"],
                Capabilities = ["tool:echo:invoke:echo"]
            }
        }
    };

    private sealed class DurableSiloFactory(string directory, bool requireApproval = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Weave:LocalMode", "true");
            builder.UseSetting("Weave:ActorStorage:Provider", "sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Join(directory, "actors.db")};Pooling=False");
            builder.UseSetting("Weave:Invocations:DatabasePath", Path.Join(directory, "invocations.db"));
            builder.UseSetting("urls", "http://127.0.0.1:0");
            builder.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }));
            if (requireApproval)
                builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                    options.ApprovalRequiredGrants = ["tool:echo:invoke:echo"]));
        }
    }

    private sealed class McpPeer(string label) : IAsyncDisposable
    {
        private readonly WebApplication _app = CreateApp();
        private int _calls;

        public string SchemaDescription { get; set; } = "Return text unchanged.";
        public string AnnotationTitle { get; set; } = "Echo";
        public string ServerVersion { get; set; } = EchoServerVersion;
        public string Endpoint { get; private set; } = string.Empty;
        public int CallCount => Volatile.Read(ref _calls);

        private static WebApplication CreateApp()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            return builder.Build();
        }

        public async Task StartAsync()
        {
            _app.MapPost("/mcp", async (Microsoft.AspNetCore.Http.HttpRequest request) =>
            {
                using var body = await JsonDocument.ParseAsync(request.Body);
                var root = body.RootElement;
                var method = root.GetProperty("method").GetString();
                if (method == "notifications/initialized")
                    return Microsoft.AspNetCore.Http.Results.Accepted();
                var id = root.GetProperty("id").GetInt64();
                object result = method switch
                {
                    "initialize" => new
                    {
                        protocolVersion = "2024-11-05",
                        serverInfo = new { name = "weave-plugin-echo", version = ServerVersion }
                    },
                    "tools/list" => new
                    {
                        tools = new[] { new { name = "echo", description = SchemaDescription,
                            inputSchema = new { type = "object", properties = new { text = new { type = "string" } } },
                            annotations = new { title = AnnotationTitle } } }
                    },
                    "tools/call" => Call(root),
                    _ => throw new InvalidOperationException($"Unexpected MCP method {method}.")
                };
                return Microsoft.AspNetCore.Http.Results.Json(new { jsonrpc = "2.0", id, result });
            });
            await _app.StartAsync(TestContext.Current.CancellationToken);
            var address = _app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            Endpoint = new Uri(new Uri(address), "/mcp").ToString();
        }

        private object Call(JsonElement root)
        {
            Interlocked.Increment(ref _calls);
            var text = root.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString();
            return new { content = new[] { new { type = "text", text = label == "original" ? text : label } } };
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
