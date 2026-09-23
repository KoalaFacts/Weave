using System.Net;
using System.Text;
using Weave.Invocations;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task Review_OriginalRequest_ReturnsBoundContentWithoutApprovingOrExecuting()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request() with { RawInput = "Reviewed text: <script>alert('not executable')</script>\n第二行" };
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reviewer = fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request, reviewer);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
        var body = await JsonAsync(response);
        body.GetProperty("subject").GetString().ShouldBe("external-agent");
        body.GetProperty("toolName").GetString().ShouldBe("files");
        body.GetProperty("operation").GetString().ShouldBe("write_file");
        body.GetProperty("parameters").GetProperty("path").GetString().ShouldBe("note.txt");
        body.GetProperty("rawInput").GetString().ShouldBe(request.RawInput);
        body.GetProperty("targetDescription").GetString().ShouldNotBeNull()
            .ShouldContain(Path.GetDirectoryName(fx.Target)!);
        var approval = (await fx.Tool.GetApprovalAsync(request.InvocationId!.Value, reviewer)).ShouldNotBeNull();
        body.GetProperty("planDigest").GetString().ShouldBe(approval.PlanDigest);
        approval.State.ShouldBe(InvocationApprovalState.Pending);
        (await fx.Tool.GetInvocationAsync(request.InvocationId.Value, fx.Token())).ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
        var wire = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        wire.ShouldNotContain("<script>");
        wire.ShouldNotContain(reviewer.Signature);

        // The verified preview is not a new approval mechanism or an execution grant.
        (await fx.Tool.DecideApprovalAsync(request.InvocationId.Value, approval.PlanDigest,
            InvocationApprovalDecision.Approve, reviewer)).Succeeded.ShouldBeTrue();
        using var executed = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        executed.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(fx.Target).ShouldBe(request.RawInput);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("path")]
    [InlineData("extra-parameter")]
    public async Task Review_ChangedContent_ReturnsConflictWithoutEchoingUnverifiedInput(string change)
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var changed = change switch
        {
            "content" => request with { RawInput = "unverified private content" },
            "path" => request with { Parameters = new() { ["path"] = "unverified private path" } },
            _ => request with { Parameters = new() { ["path"] = "note.txt", ["extra"] = "unverified private value" } }
        };
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), changed,
            fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("unverified private");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("missing-credential", HttpStatusCode.Unauthorized)]
    [InlineData("tampered", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("missing-decision", HttpStatusCode.Forbidden)]
    [InlineData("wrong-operation", HttpStatusCode.Forbidden)]
    [InlineData("self-review", HttpStatusCode.Forbidden)]
    public async Task Review_UnauthorizedReviewer_CannotObtainVerifiedContent(string condition, HttpStatusCode expected)
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = fx.Token(condition == "self-review" ? "external-agent" : "operator",
            condition == "missing-decision" ? ["invocation:read", "tool:files:approve:write_file"]
            : condition == "wrong-operation" ? ["invocation:read", "approval:decide", "tool:files:approve:read_file"]
            : ["invocation:read", "approval:decide", "tool:files:approve:write_file"], expired: condition == "expired");
        if (condition == "tampered")
            token = token with { IssuedTo = "forged" };
        using var message = new HttpRequestMessage(HttpMethod.Post, ReviewRoute(fx, request));
        message.Content = System.Net.Http.Json.JsonContent.Create(request, options: JsonOptions);
        if (condition != "missing-credential")
            message.Headers.Add("X-Weave-Capability", Encode(token));
        using var response = await fx.Client.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("reviewed text");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Review_TargetReconnectedElsewhere_RejectsOldPlan()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var otherRoot = Path.Combine(Path.GetDirectoryName(fx.Target)!, "other");
        Directory.CreateDirectory(otherRoot);
        await fx.Tool.ConnectAsync(new ToolSpec
        {
            Name = "files",
            Type = ToolType.FileSystem,
            FileSystem = new Weave.Tools.Connectors.FileSystemToolConfig { Root = otherRoot }
        }, fx.Token(grants: ["tool:files:connect"]));
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request,
            fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        File.ReadAllText(fx.Target).ShouldBe("original");
        File.Exists(Path.Combine(otherRoot, "note.txt")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Review_OversizedBody_IsBoundedWithOrWithoutContentLength(bool knownLength)
    {
        await using var fx = new Fixture(requireApproval: true);
        var request = Request();
        var bytes = Encoding.UTF8.GetBytes(new string(' ', 1_048_577));
        using HttpContent content = knownLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), content,
            fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]));
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Review_DisabledIngress_DoesNotExposeRoute()
    {
        await using var fx = new Fixture(enabled: false);
        var request = Request();
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request, fx.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string ReviewRoute(Fixture fx, ToolInvocation request) =>
        fx.Route + "/" + request.InvocationId + "/approval/review";
}
