using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
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
                firstId = await StartWorkspaceAsync(client, firstPeer);
                secondId = await StartWorkspaceAsync(client, secondPeer);
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
                using var disable = await client.DeleteAsync($"/api/plugins/{firstId.ToUpperInvariant()}/ECHO_SERVER",
                    TestContext.Current.CancellationToken);
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
                using var enable = await client.PostAsJsonAsync("/api/plugins", new
                {
                    Name = $"{firstId}/ECHO_SERVER",
                    Type = "mcp_tools"
                }, TestContext.Current.CancellationToken);
                enable.StatusCode.ShouldBe(HttpStatusCode.Created);
                await InvokeAsync(second.Services, firstId, "first");
                using var disableAgain = await client.DeleteAsync($"/api/plugins/{firstId}/ECHO_SERVER",
                    TestContext.Current.CancellationToken);
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
            var workspaceId = await StartWorkspaceAsync(client, peer);
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
            var workspaceId = await StartWorkspaceAsync(client, peer);
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

            using var disable = await client.DeleteAsync($"/api/plugins/{workspaceId}/ECHO_SERVER",
                TestContext.Current.CancellationToken);
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
            await using var host = new DurableSiloFactory(directory);
            using var client = host.CreateClient();
            var workspaceId = await StartWorkspaceAsync(client, endpoint);
            await InvokeAsync(host.Services, workspaceId, "package response");
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

    private static Task<string> StartWorkspaceAsync(HttpClient client, McpPeer peer) =>
        StartWorkspaceAsync(client, peer.Endpoint);

    private static async Task<string> StartWorkspaceAsync(HttpClient client, string endpoint)
    {
        var manifest = new WorkspaceManifest
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
                        ["server_version"] = "0.1.0",
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
        using var response = await client.PostAsJsonAsync("/api/workspaces", new { Manifest = manifest },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("workspaceId").GetString()!;
    }

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
        public string ServerVersion { get; set; } = "0.1.0";
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
