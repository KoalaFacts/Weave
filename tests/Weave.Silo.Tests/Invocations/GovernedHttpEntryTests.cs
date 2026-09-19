using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class GovernedHttpEntryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Post_ReadWithExactGrant_ReturnsRealFileThroughHttp()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        using var response = await fx.SendAsync(HttpMethod.Post, fx.Route, Request("read_file"), fx.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("output").GetString().ShouldBe("original");
        body.GetProperty("outcome").GetString().ShouldBe("Succeeded");
    }

    [Fact]
    public async Task Post_WriteWithoutAuthority_DoesNotDispatch()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        using var response = await fx.SendAsync(HttpMethod.Post, fx.Route, Request(),
            fx.Token(grants: ["tool:files:invoke:read_file"]));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("malformed", HttpStatusCode.Unauthorized)]
    [InlineData("tampered", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("revoked", HttpStatusCode.Unauthorized)]
    [InlineData("duplicate", HttpStatusCode.Unauthorized)]
    [InlineData("workspace", HttpStatusCode.Forbidden)]
    public async Task Post_InvalidCredential_CannotExecuteInAnonymousDevelopmentMode(string condition, HttpStatusCode status)
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var token = fx.Token(expired: condition == "expired");
        if (condition == "revoked")
            fx.Tokens.Revoke(token.TokenId);
        if (condition == "tampered")
            token = token with { IssuedTo = "forged-subject" };
        using var message = new HttpRequestMessage(HttpMethod.Post,
            condition == "workspace" ? fx.Route.Replace(fx.Workspace, "foreign", StringComparison.Ordinal) : fx.Route);
        message.Content = JsonContent.Create(Request(), options: JsonOptions);
        if (condition != "missing")
        {
            var header = condition == "malformed" ? "not-a-valid-capability" : Encode(token);
            message.Headers.TryAddWithoutValidation("X-Weave-Capability", header);
            if (condition == "duplicate")
                message.Headers.TryAddWithoutValidation("X-Weave-Capability", header);
        }
        using var response = await fx.Client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(status);
        File.ReadAllText(fx.Target).ShouldBe("original");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain(token.Signature);
        text.ShouldNotContain("forged-subject");
    }

    [Fact]
    public async Task Post_ApprovalAndResubmission_UsesExistingGovernanceAndNeverReplaysEffect()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var pendingBody = await JsonAsync(pending);
        pendingBody.GetProperty("errorCode").GetString().ShouldBe("approval-pending");
        pendingBody.TryGetProperty("attemptId", out _).ShouldBeFalse();
        File.ReadAllText(fx.Target).ShouldBe("original");
        pending.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();

        using var status = await fx.SendAsync(HttpMethod.Get, fx.Route + "/" + request.InvocationId + "/approval", null, fx.Token());
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(status)).GetProperty("approvalState").GetString().ShouldBe("Pending");
        using var attempt = await fx.SendAsync(HttpMethod.Get, fx.Route + "/" + request.InvocationId, null, fx.Token());
        attempt.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Operator decisions still use the protected backend, not an unexplained-hash HTTP approval button.
        var approver = fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        var approval = (await fx.Tool.GetApprovalAsync(request.InvocationId!.Value, approver)).ShouldNotBeNull();
        (await fx.Tool.DecideApprovalAsync(request.InvocationId.Value, approval.PlanDigest,
            InvocationApprovalDecision.Approve, approver)).Succeeded.ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("original");

        using var executed = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        executed.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(fx.Target).ShouldBe("reviewed text");
        File.WriteAllText(fx.Target, "later external change");
        using var duplicate = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(duplicate)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("later external change");
        using var completed = await fx.SendAsync(HttpMethod.Get, fx.Route + "/" + request.InvocationId, null, fx.Token());
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var metadata = await completed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        metadata.ShouldContain("Succeeded");
        metadata.ShouldNotContain("reviewed text");
        metadata.ShouldNotContain("signature");
        metadata.ShouldNotContain("tokenId");
    }

    [Fact]
    public async Task Post_ChangedInputForRecordedId_ReturnsConflictWithoutASecondEffect()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request();
        using var first = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var conflict = await fx.SendAsync(HttpMethod.Post, fx.Route, request with { RawInput = "changed input" }, fx.Token());
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(conflict)).GetProperty("errorCode").GetString().ShouldBe("invocation-id-conflict");
        File.ReadAllText(fx.Target).ShouldBe("reviewed text");
    }

    [Fact]
    public async Task Get_DifferentSubject_CannotReadOriginalInvocation()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request();
        using var first = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var response = await fx.SendAsync(HttpMethod.Get, fx.Route + "/" + request.InvocationId, null, fx.Token("another-agent"));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("wrong-tool")]
    [InlineData("null-parameters")]
    [InlineData("bad-json")]
    public async Task Post_InvalidInput_DoesNotWrite(string condition)
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var request = Request();
        request = condition switch
        {
            "missing-id" => request with { InvocationId = null },
            "wrong-tool" => request with { ToolName = "another-tool" },
            "null-parameters" => request with { Parameters = null! },
            _ => request
        };
        using var response = await fx.SendAsync(HttpMethod.Post, fx.Route,
            condition == "bad-json" ? new StringContent("{", Encoding.UTF8, "application/json") : request, fx.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_OversizedBodyWithOrWithoutContentLength_RejectsBeforeDispatch(bool chunked)
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var payload = JsonSerializer.SerializeToUtf8Bytes(Request() with { RawInput = new string('x', 1_048_577) }, JsonOptions);
        using HttpContent content = chunked ? new UnknownLengthContent(payload) : new ByteArrayContent(payload);
        content.Headers.ContentType = new("application/json");
        using var response = await fx.SendAsync(HttpMethod.Post, fx.Route, content, fx.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Post_DefaultDisabled_DoesNotExposeTheRoute()
    {
        await using var fx = new Fixture(enabled: false);
        using var response = await fx.SendAsync(HttpMethod.Post, fx.Route, Request(), fx.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static ToolInvocation Request(string method = "write_file") => new()
    {
        InvocationId = InvocationId.From(Guid.NewGuid().ToString("N")),
        ToolName = "files",
        Method = method,
        Parameters = new() { ["path"] = "note.txt" },
        RawInput = method == "read_file" ? null : "reviewed text"
    };

    private static string Encode(CapabilityToken token) => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload, CancellationToken.None).AsTask();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-http-entry-{Guid.NewGuid():N}");
        private readonly SiloFactory _parent = new();
        private readonly WebApplicationFactory<Program> _host;
        public HttpClient Client { get; }
        public string Workspace { get; } = "http-" + Guid.NewGuid().ToString("N");
        public string Route => $"/api/workspaces/{Workspace}/tools/files/invocations";
        public string Target => Path.Combine(_root, "tools", "note.txt");
        public ICapabilityTokenService Tokens => _host.Services.GetRequiredService<ICapabilityTokenService>();
        public IToolActor Tool => _host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From(Workspace + "/files"));

        public Fixture(bool enabled = true, bool requireApproval = false)
        {
            Directory.CreateDirectory(Path.Combine(_root, "tools"));
            File.WriteAllText(Target, "original");
            _host = _parent.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Weave:Auth:Mode", "none");
                builder.UseSetting("Weave:Invocations:Http:Enabled", enabled ? "true" : "false");
                builder.ConfigureServices(services => services.PostConfigure<InvocationJournalOptions>(options =>
                {
                    options.DatabasePath = Path.Combine(_root, "journal.db");
                    options.ApprovalRequiredGrants = requireApproval ? ["tool:files:invoke:write_file"] : [];
                }));
            });
            Client = _host.CreateClient();
        }

        public Task<ToolHandle> ConnectAsync() => Tool.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = Path.Combine(_root, "tools") }
        }, Token(grants: ["tool:files:connect"]));

        public CapabilityToken Token(string subject = "external-agent", HashSet<string>? grants = null, bool expired = false) =>
            Tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = Workspace,
                IssuedTo = subject,
                Grants = grants ?? ["tool:files:invoke:read_file", "tool:files:invoke:write_file", "invocation:read"],
                Lifetime = expired ? TimeSpan.FromMinutes(-1) : TimeSpan.FromMinutes(5)
            });

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body, CapabilityToken token)
        {
            using var request = new HttpRequestMessage(method, route);
            request.Headers.Add("X-Weave-Capability", Encode(token));
            if (body is not null)
                request.Content = body as HttpContent ?? JsonContent.Create(body, options: JsonOptions);
            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
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
