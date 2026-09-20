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
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class ReviewedApprovalDecisionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("approve", InvocationApprovalState.Approved)]
    [InlineData("reject", InvocationApprovalState.Rejected)]
    public async Task Decide_VerifiedOriginalRequest_RecordsDecisionWithoutExecuting(string decision, InvocationApprovalState state)
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        using var preview = await fx.SendAsync(fx.Route + "/approval/review", fx.Request, fx.Reviewer());
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        var digest = (await JsonAsync(preview)).GetProperty("planDigest").GetString()!;

        using var response = await fx.DecideAsync(decision, digest);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("approvalState").GetString().ShouldBe(state.ToString());
        body.GetProperty("invocationId").GetString().ShouldBe(fx.Request.InvocationId!.Value.ToString());
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        (await fx.CurrentAsync()).State.ShouldBe(state);
        (await fx.Tool.GetInvocationAsync(fx.Request.InvocationId.Value, fx.Writer())).ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain("reviewed content");
        text.ShouldNotContain("signature");
        text.ShouldNotContain("tokenId");

        using var resumed = await fx.SendAsync(fx.InvocationsRoute, fx.Request, fx.Writer());
        resumed.StatusCode.ShouldBe(decision == "approve" ? HttpStatusCode.OK : HttpStatusCode.Conflict);
        File.ReadAllText(fx.Target).ShouldBe(decision == "approve" ? "reviewed content" : "original");
    }

    [Theory]
    [InlineData("body")]
    [InlineData("digest")]
    [InlineData("target")]
    public async Task Decide_ContentOrTargetChangedAfterPreview_RejectsWithoutDecision(string change)
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var original = await fx.CurrentAsync();
        var request = change == "body" ? fx.Request with { RawInput = "unverified private change" } : fx.Request;
        if (change == "target")
            await fx.ConnectAsync(Path.Combine(fx.ToolRoot, "other"));

        using var response = await fx.DecideAsync("approve",
            change == "digest" ? "approval-v1:incorrect-digest" : original.PlanDigest, request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("unverified private change");
        (await fx.CurrentAsync()).ShouldBe(original);
        (await fx.Tool.GetInvocationAsync(fx.Request.InvocationId!.Value, fx.Writer())).ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("self")]
    [InlineData("read-only")]
    public async Task Decide_UnauthorizedPrincipal_CannotTurnReviewIntoAuthority(string condition)
    {
        await using var fx = new Fixture();
        await fx.RequestApprovalAsync();
        var original = await fx.CurrentAsync();
        var token = condition == "self" ? fx.Reviewer("writer") : fx.Token("operator", ["invocation:read"]);

        using var response = await fx.DecideAsync("approve", original.PlanDigest, token: token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fx.CurrentAsync()).ShouldBe(original);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Decide_DefaultDisabled_DoesNotExposeMutationRoute()
    {
        await using var fx = new Fixture(decisionsEnabled: false);
        await fx.RequestApprovalAsync();
        var original = await fx.CurrentAsync();
        using var response = await fx.DecideAsync("approve", original.PlanDigest);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fx.CurrentAsync()).ShouldBe(original);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-reviewed-decision-{Guid.NewGuid():N}");
        private readonly SiloFactory _parent = new();
        private readonly WebApplicationFactory<Program> _host;
        public string Workspace { get; } = "decision-" + Guid.NewGuid().ToString("N");
        public string ToolRoot => Path.Combine(_root, "tools");
        public string Target => Path.Combine(ToolRoot, "note.txt");
        public string DatabasePath => Path.Combine(_root, "journal.db");
        public string InvocationsRoute => $"/api/workspaces/{Workspace}/tools/files/invocations";
        public string Route => InvocationsRoute + "/" + Request.InvocationId;
        public HttpClient Client { get; }
        public ICapabilityTokenService Tokens => _host.Services.GetRequiredService<ICapabilityTokenService>();
        public IToolActor Tool => _host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From(Workspace + "/files"));
        public ToolInvocation Request { get; } = new()
        {
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
            ToolName = "files",
            Method = "write_file",
            Parameters = new() { ["path"] = "note.txt" },
            RawInput = "reviewed content"
        };

        public Fixture(bool decisionsEnabled = true)
        {
            Directory.CreateDirectory(ToolRoot);
            File.WriteAllText(Target, "original");
            _host = _parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", "none");
                builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
                if (decisionsEnabled)
                    builder.UseSetting("Weave:Invocations:Http:DecisionsEnabled", "true");
                builder.UseSetting("CapabilityTokens:SigningKey", "test-reviewed-decision-" + Guid.NewGuid().ToString("N"));
                builder.UseSetting("CapabilityTokens:RevocationDirectory", Path.Combine(_root, "revocations"));
                builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                {
                    options.DatabasePath = DatabasePath;
                    options.ApprovalRequiredGrants = ["tool:files:invoke:write_file"];
                }));
            });
            Client = _host.CreateClient();
        }

        public CapabilityToken Token(string subject, HashSet<string> grants) => Tokens.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = Workspace,
            IssuedTo = subject,
            Grants = grants,
            Lifetime = TimeSpan.FromMinutes(5)
        });

        public CapabilityToken Writer() => Token("writer", ["tool:files:invoke:write_file", "invocation:read"]);
        public CapabilityToken Reviewer(string subject = "operator") =>
            Token(subject, ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);

        public Task<ToolHandle> ConnectAsync(string? root = null) => Tool.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = root ?? ToolRoot }
        }, Token("setup", ["tool:files:connect"]));

        public async Task RequestApprovalAsync()
        {
            await ConnectAsync();
            using var pending = await SendAsync(InvocationsRoute, Request, Writer());
            pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        public async Task<InvocationApproval> CurrentAsync() =>
            (await Tool.GetApprovalAsync(Request.InvocationId!.Value, Reviewer())).ShouldNotBeNull();

        public Task<HttpResponseMessage> DecideAsync(string decision, string digest,
            ToolInvocation? request = null, CapabilityToken? token = null) =>
            SendAsync(Route + "/approval/decision", new
            {
                Invocation = request ?? Request,
                PlanDigest = digest,
                Decision = decision
            }, token ?? Reviewer());

        public async Task<HttpResponseMessage> SendAsync(string route, object body, CapabilityToken token)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, route);
            message.Headers.Add("X-Weave-Capability", WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions)));
            message.Content = body as HttpContent ?? JsonContent.Create(body, options: JsonOptions);
            return await Client.SendAsync(message, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.DisposeAsync();
            await _parent.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }
}
