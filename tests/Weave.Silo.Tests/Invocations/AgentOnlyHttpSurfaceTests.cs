using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class AgentOnlyHttpSurfaceTests
{
    private const string Prefix = "/api/workspaces/workspace/tools/files/invocations";
    private const string Pattern = "/api/workspaces/{workspaceId}/tools/{toolName}/invocations";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Start_AgentOnly_RegistersOnlyFiveGovernedApiRoutes()
    {
        await using var fx = new Fixture();
        _ = fx.Client;
        var routes = fx.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText!).ToArray();
        routes.Where(r => r.StartsWith("/api", StringComparison.Ordinal)).Order().ToArray()
            .ShouldBe(new[] { Pattern + "/", Pattern + "/{invocationId}", Pattern + "/{invocationId}/approval",
                Pattern + "/{invocationId}/proposal", Pattern + "/{invocationId}/resume" }.Order().ToArray());
        routes.ShouldNotContain(r => r.Contains("openapi", StringComparison.OrdinalIgnoreCase)
            || r.Contains("scalar", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("GET", "/api/workspaces/")]
    [InlineData("GET", "/api/plugins/")]
    [InlineData("GET", "/api/workspaces/workspace/tools/")]
    [InlineData("GET", "/openapi/v1.json")]
    [InlineData("GET", "/scalar/v1")]
    [InlineData("POST", Prefix + "/14bd2b92c8a34fdd8c79cb62eab32a13/approval/review")]
    [InlineData("POST", Prefix + "/14bd2b92c8a34fdd8c79cb62eab32a13/approval/decision")]
    [InlineData("GET", Prefix + "/14bd2b92c8a34fdd8c79cb62eab32a13/approval/review")]
    [InlineData("POST", Prefix + "/14bd2b92c8a34fdd8c79cb62eab32a13/decision")]
    public async Task Send_AgentOnly_AdministrativeAndOperatorRoutesAreAbsent(string method, string path)
    {
        await using var fx = new Fixture();
        using var message = new HttpRequestMessage(new HttpMethod(method), path);
        message.Headers.Add("X-Weave-Capability", Encode(fx.Token("operator",
            ["invocation:read", "approval:decide", "tool:files:approve:write_file"])));
        if (method == "POST")
            message.Content = JsonContent.Create(new { });
        using var response = await fx.Client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invoke_AgentOnly_KeepsReadDenyPendingAndScopedQueriesOnExistingPath()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var reader = fx.Token("agent", ["tool:files:invoke:read_file", "invocation:read"]);
        var read = Request("read_file");
        using var allowed = await fx.SendAsync(read, reader);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(allowed)).GetProperty("output").GetString().ShouldBe("original");
        var write = Request("write_file");
        using var denied = await fx.SendAsync(write, reader);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        File.ReadAllText(fx.Target).ShouldBe("original");
        var writer = fx.Token("agent", ["tool:files:invoke:write_file", "invocation:read"]);
        using var pending = await fx.SendAsync(write, writer);
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = await JsonAsync(pending);
        result.GetProperty("errorCode").GetString().ShouldBe("approval-pending");
        result.TryGetProperty("attemptId", out _).ShouldBeFalse();
        File.ReadAllText(fx.Target).ShouldBe("original");
        using var query = new HttpRequestMessage(HttpMethod.Get, pending.Headers.Location);
        query.Headers.Add("X-Weave-Capability", Encode(writer));
        using var status = await fx.Client.SendAsync(query, TestContext.Current.CancellationToken);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(status)).GetProperty("approvalState").GetString().ShouldBe("Pending");
        using var outcome = new HttpRequestMessage(HttpMethod.Get, Prefix + "/" + read.InvocationId);
        outcome.Headers.Add("X-Weave-Capability", Encode(reader));
        using var recorded = await fx.Client.SendAsync(outcome, TestContext.Current.CancellationToken);
        recorded.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(recorded)).GetProperty("outcome").GetString().ShouldBe("Succeeded");
    }

    [Theory]
    [InlineData(false, true, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, HttpStatusCode.OK)]
    public async Task Invoke_AgentOnly_GlobalAndCapabilityAuthenticationRemainAdditive(
        bool global, bool capability, HttpStatusCode expected)
    {
        await using var fx = new Fixture(globalAuthentication: true);
        await fx.ConnectAsync();
        using var message = new HttpRequestMessage(HttpMethod.Post, Prefix);
        message.Content = JsonContent.Create(Request("read_file"), options: JsonOptions);
        if (global)
            message.Headers.Authorization = new("Bearer", fx.GlobalSecret);
        if (capability)
            message.Headers.Add("X-Weave-Capability", Encode(fx.Token("agent", ["tool:files:invoke:read_file"])));
        using var response = await fx.Client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Start_ContradictoryAgentOnlyConfiguration_FailsInsteadOfExposingAFullHost(bool enabled, bool decisions)
    {
        await using var fx = new Fixture(enabled: enabled, decisions: decisions);
        var failure = Should.Throw<InvalidOperationException>(() => { _ = fx.Client; });
        failure.Message.ShouldContain("AgentOnly");
    }

    [Fact]
    public async Task Start_DefaultFullHost_PreservesExistingAdministrativeAndReviewRoutes()
    {
        await using var fx = new Fixture(agentOnly: false);
        using var response = await fx.Client.GetAsync("/api/workspaces/", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var routes = fx.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!).ToArray();
        routes.ShouldContain(Pattern + "/{invocationId}/approval/review");
    }

    private static string Encode(CapabilityToken token) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions));
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    private static ToolInvocation Request(string method) => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = method,
        Parameters = new() { ["path"] = "note.txt" },
        RawInput = method == "read_file" ? null : "not yet approved"
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-agent-only-{Guid.NewGuid():N}");
        private readonly SiloFactory _parent = new();
        private HttpClient? _client;
        public WebApplicationFactory<Program> Host { get; }
        public HttpClient Client => _client ??= Host.CreateClient();
        public string Target => Path.Combine(_root, "tools", "note.txt");
        public string GlobalSecret { get; } = "test-global-" + Guid.NewGuid().ToString("N");
        private IToolActor Tool => Host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From("workspace/files"));

        public Fixture(bool agentOnly = true, bool enabled = true, bool decisions = false, bool globalAuthentication = false)
        {
            Directory.CreateDirectory(Path.Combine(_root, "tools"));
            File.WriteAllText(Target, "original");
            Host = _parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", globalAuthentication ? "bearer" : "none");
                if (globalAuthentication)
                    builder.UseSetting("Weave:Auth:Secret", GlobalSecret);
                builder.UseSetting("Weave:Invocations:Http:Enabled", enabled.ToString());
                builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", decisions.ToString());
                if (agentOnly)
                    builder.UseSetting("Weave:Invocations:Http:AgentOnly", "true");
                builder.UseSetting("CapabilityTokens:SigningKey", "test-agent-only-" + Guid.NewGuid().ToString("N"));
                builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(_root, "revocations"));
                builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                {
                    options.DatabasePath = Path.Combine(_root, "journal.db");
                    options.ApprovalRequiredGrants = ["tool:files:invoke:write_file"];
                }));
            });
        }

        public CapabilityToken Token(string subject, HashSet<string> grants) =>
            Host.Services.GetRequiredService<ICapabilityTokenService>().Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "workspace",
                IssuedTo = subject,
                Grants = grants,
                Lifetime = TimeSpan.FromMinutes(5)
            });
        public Task<ToolHandle> ConnectAsync() => Tool.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = Path.GetDirectoryName(Target)! }
        }, Token("setup", ["tool:files:connect"]));
        public async Task<HttpResponseMessage> SendAsync(ToolInvocation request, CapabilityToken token)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Prefix);
            message.Headers.Add("X-Weave-Capability", Encode(token));
            message.Content = JsonContent.Create(request, options: JsonOptions);
            return await Client.SendAsync(message, TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await Host.DisposeAsync();
            await _parent.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }
}
