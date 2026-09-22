using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class TrustedOperatorOnboardingTests
{
    private const string Route = "/api/workspaces/onboarding/tools/files/invocations";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly string[] EscalatedGrants = ["*"];
    private static readonly string[] ReaderGrants = ["invocation:read", "tool:files:invoke:read_file"];

    [Fact]
    public async Task Onboard_RealHttpOperatorAndAgent_ShareApprovalAndNeverReplayTheEffect()
    {
        await using var fx = new Fixture();
        fx.Start(useKestrel: true);
        using var connected = await fx.SendAsync(HttpMethod.Post, "/api/operator/tools/files/connect", admin: true);
        connected.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var writer = await fx.IssueAsync("writer");
        var reader = await fx.IssueAsync("reader");
        var reviewer = await fx.IssueAsync("reviewer");
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, Route, request, writer);
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        File.ReadAllText(fx.Target).ShouldBe("original");
        var approvalRoute = Route + "/" + request.InvocationId + "/approval";
        using var status = await fx.SendAsync(HttpMethod.Get, approvalRoute, capability: writer);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(status)).GetProperty("approvalState").GetString().ShouldBe("Pending");

        // Having an execution credential is not permission to enter the operator surface.
        using var noOperator = await fx.SendAsync(HttpMethod.Post, approvalRoute + "/review", request, writer);
        noOperator.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var wrongReviewer = await fx.SendAsync(HttpMethod.Post, approvalRoute + "/review", request, writer, admin: true);
        wrongReviewer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var review = await fx.SendAsync(HttpMethod.Post, approvalRoute + "/review", request, reviewer, admin: true);
        review.StatusCode.ShouldBe(HttpStatusCode.OK);
        var digest = (await JsonAsync(review)).GetProperty("planDigest").GetString();
        using var changed = await fx.SendAsync(HttpMethod.Post, approvalRoute + "/decision",
            new { invocation = request with { RawInput = "unreviewed change" }, planDigest = digest, decision = "approve" }, reviewer, admin: true);
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var decision = await fx.SendAsync(HttpMethod.Post, approvalRoute + "/decision",
            new { invocation = request, planDigest = digest, decision = "approve" }, reviewer, admin: true);
        decision.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(fx.Target).ShouldBe("original");
        using var noGrant = await fx.SendAsync(HttpMethod.Post, Route, request, reader);
        noGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var executed = await fx.SendAsync(HttpMethod.Post, Route, request, writer);
        executed.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(fx.Target).ShouldBe("operator-reviewed text");
        File.WriteAllText(fx.Target, "external edit");
        using var duplicate = await fx.SendAsync(HttpMethod.Post, Route, request, writer);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(duplicate)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("external edit");
        using var outcome = await fx.SendAsync(HttpMethod.Get, Route + "/" + request.InvocationId, capability: writer);
        outcome.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(outcome)).GetProperty("outcome").GetString().ShouldBe("Succeeded");
    }

    [Theory]
    [InlineData("/api/workspaces", "GET")]
    [InlineData("/api/operator/tools/files/connect", "POST")]
    [InlineData("/api/operator/credentials/writer/issue", "POST")]
    [InlineData("/openapi/v1.json", "GET")]
    [InlineData("/api/workspaces/onboarding/tools/files/invocations/not-an-id/approval/review", "POST")]
    public async Task Request_WithoutOperatorKey_CannotReachManagementOrMintCredentials(string route, string method)
    {
        await using var fx = new Fixture();
        fx.Start();
        using var response = await fx.SendAsync(new HttpMethod(method), route);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("duplicate")]
    public async Task Issue_InvalidOperatorKey_IsRejectedWithoutReturningCredential(string condition)
    {
        await using var fx = new Fixture();
        fx.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/operator/credentials/writer/issue");
        request.Headers.TryAddWithoutValidation("X-Weave-Operator-Key", condition == "wrong" ? "wrong-key" : fx.OperatorKey);
        if (condition == "duplicate")
            request.Headers.TryAddWithoutValidation("X-Weave-Operator-Key", fx.OperatorKey);
        using var response = await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(fx.OperatorKey);
        body.ShouldNotContain("signature");
    }

    [Theory]
    [InlineData("tools/not-configured/connect")]
    [InlineData("credentials/not-configured/issue")]
    public async Task Post_UnknownConfiguredProfile_ReturnsNotFound(string operation)
    {
        await using var fx = new Fixture();
        fx.Start();
        using var response = await fx.SendAsync(HttpMethod.Post, "/api/operator/" + operation, admin: true);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Issue_PredefinedProfile_RejectsCallerAuthorityAndReturnsNoncacheableCredential()
    {
        await using var fx = new Fixture();
        fx.Start();
        using var injection = await fx.SendAsync(HttpMethod.Post, "/api/operator/credentials/reader/issue",
            new { issuedTo = "administrator", grants = EscalatedGrants }, admin: true);
        injection.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var response = await fx.SendAsync(HttpMethod.Post, "/api/operator/credentials/reader/issue", admin: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var encoded = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var token = JsonSerializer.Deserialize<CapabilityToken>(WebEncoders.Base64UrlDecode(encoded), JsonOptions)!;
        token.IssuedTo.ShouldBe("agent");
        token.WorkspaceId.ShouldBe("onboarding");
        token.Grants.Order(StringComparer.Ordinal).ShouldBe(ReaderGrants);
        (token.ExpiresAt - token.IssuedAt).ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("same-signing-key")]
    [InlineData("agent-only")]
    [InlineData("disabled-invocations")]
    [InlineData("invalid-lifetime")]
    public async Task Start_UnsafeOperatorConfiguration_FailsWithoutOpeningAdminSurface(string condition)
    {
        await using var fx = new Fixture(condition: condition);
        var error = Record.Exception(() => fx.Start());
        error.ShouldNotBeNull();
        error.ToString().ShouldNotContain(fx.OperatorKey);
    }

    [Fact]
    public async Task Start_OperatorDisabledAndAgentOnly_KeepsOperatorRoutesAbsent()
    {
        await using var fx = new Fixture(enabled: false, condition: "agent-only");
        fx.Start();
        using var response = await fx.SendAsync(HttpMethod.Post, "/api/operator/credentials/writer/issue", admin: true);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var management = await fx.SendAsync(HttpMethod.Get, "/api/workspaces", admin: true);
        management.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static ToolInvocation Request() => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = "write_file",
        Parameters = new() { ["path"] = "note.txt" },
        RawInput = "operator-reviewed text"
    };

    private static Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "weave-operator-" + Guid.NewGuid().ToString("N"));
        private readonly SiloFactory _parent = new();
        private readonly WebApplicationFactory<Program> _host;
        public string OperatorKey { get; } = "test-operator-" + Guid.NewGuid().ToString("N");
        public string GlobalSecret { get; } = "test-platform-" + Guid.NewGuid().ToString("N");
        public HttpClient Client { get; private set; } = null!;
        public string Target => Path.Combine(_root, "tools", "note.txt");

        public Fixture(bool enabled = true, string? condition = null, bool globalAuthentication = false)
        {
            Directory.CreateDirectory(Path.Combine(_root, "tools"));
            File.WriteAllText(Target, "original");
            var signingKey = "test-signing-" + Guid.NewGuid().ToString("N");
            _host = _parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", globalAuthentication ? "bearer" : "none");
                if (globalAuthentication)
                    builder.UseSetting("Weave:Auth:Secret", GlobalSecret);
                builder.UseSetting("CapabilityTokens:SigningKey", signingKey);
                builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(_root, "revocations"));
                builder.UseSetting("Weave:Invocations:Http:Enabled", condition == "disabled-invocations" ? "false" : "true");
                builder.UseSetting("Weave:Invocations:Http:AgentOnly", condition == "agent-only" ? "true" : "false");
                builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", condition == "agent-only" ? "false" : "true");
                builder.UseSetting("Weave:Operator:Enabled", enabled ? "true" : "false");
                builder.UseSetting("Weave:Operator:Key", condition == "missing-key" ? "" : condition == "same-signing-key" ? signingKey : OperatorKey);
                builder.UseSetting("Weave:Operator:Tools:files:WorkspaceId", "onboarding");
                builder.UseSetting("Weave:Operator:Tools:files:Tool:Name", "files");
                builder.UseSetting("Weave:Operator:Tools:files:Tool:Type", "FileSystem");
                builder.UseSetting("Weave:Operator:Tools:files:Tool:FileSystem:Root", Path.Combine(_root, "tools"));
                Profile("writer", "agent", ["tool:files:invoke:write_file", "invocation:read"]);
                Profile("reader", "agent", ["tool:files:invoke:read_file", "invocation:read"]);
                Profile("reviewer", "operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
                builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                {
                    options.DatabasePath = Path.Combine(_root, "journal.db");
                    options.ApprovalRequiredGrants = ["tool:files:invoke:write_file"];
                }));
                void Profile(string name, string subject, string[] grants)
                {
                    var prefix = "Weave:Operator:Credentials:" + name + ":";
                    builder.UseSetting(prefix + "WorkspaceId", "onboarding");
                    builder.UseSetting(prefix + "IssuedTo", subject);
                    builder.UseSetting(prefix + "Lifetime", condition == "invalid-lifetime" ? "00:00:00" : "00:05:00");
                    for (var index = 0; index < grants.Length; index++)
                        builder.UseSetting(prefix + "Grants:" + index, grants[index]);
                }
            });
        }

        public void Start(bool useKestrel = false)
        {
            if (useKestrel)
            {
                _host.UseKestrel(0);
                Client = _host.CreateClient();
            }
            else
                Client = _host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        }

        public async Task<string> IssueAsync(string name)
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/operator/credentials/" + name + "/issue", admin: true);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body = null, string? capability = null, bool admin = false)
        {
            using var request = new HttpRequestMessage(method, route);
            if (admin)
                request.Headers.Add("X-Weave-Operator-Key", OperatorKey);
            if (capability is not null)
                request.Headers.Add("X-Weave-Capability", capability);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: JsonOptions);
            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await _host.DisposeAsync();
            await _parent.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }
}
