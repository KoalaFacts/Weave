using System.Net;
using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task Proposal_OnlyUuidAndAuthorizedOwner_ReturnsStoredOriginalWithoutDispatch()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request() with { RawInput = "Immutable proposal <not markup> 汉🙂\n" };
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = request.InvocationId;
        request = request with { RawInput = "client copy overwritten" };

        using var response = await fx.SendAsync(HttpMethod.Get, fx.Route + "/" + id + "/proposal", null,
            fx.Token(grants: ["invocation:read", "invocation:proposal:read"]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var body = await JsonAsync(response);
        body.GetProperty("invocationId").GetString().ShouldBe(id.ToString());
        body.GetProperty("rawInput").GetString().ShouldBe("Immutable proposal <not markup> 汉🙂\n");
        body.GetProperty("parameters").GetProperty("path").GetString().ShouldBe("note.txt");
        File.ReadAllText(fx.Target).ShouldBe("original");
        (await fx.Tool.GetInvocationAsync(id!.Value, fx.Token())).ShouldBeNull();
    }

    [Fact]
    public async Task Review_UuidWithoutUploadedRequest_ReturnsVerifiedStoredProposal()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reviewer = fx.Token("human-reviewer", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);

        using var response = await fx.SendAsync(HttpMethod.Get, ReviewRoute(fx, request), null, reviewer);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("rawInput").GetString().ShouldBe(request.RawInput);
        body.GetProperty("subject").GetString().ShouldBe("external-agent");
        body.GetProperty("planDigest").GetString().ShouldNotBeNull().ShouldStartWith("approval-v1:");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData("metadata-only", HttpStatusCode.Forbidden)]
    [InlineData("other-subject", HttpStatusCode.NotFound)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("revoked", HttpStatusCode.Unauthorized)]
    [InlineData("other-workspace", HttpStatusCode.Forbidden)]
    public async Task Proposal_UnauthorizedUuidLookup_NeverDisclosesPayload(string condition, HttpStatusCode expected)
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request() with { RawInput = "restricted-proposal-marker" };
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = fx.Token(condition == "other-subject" ? "unrelated" : "external-agent",
            condition == "metadata-only" ? ["invocation:read"] : ["invocation:read", "invocation:proposal:read"],
            expired: condition == "expired");
        if (condition == "revoked")
            fx.Tokens.Revoke(token.TokenId);
        var route = fx.Route + "/" + request.InvocationId + "/proposal";
        if (condition == "other-workspace")
            route = route.Replace(fx.Workspace, "foreign", StringComparison.Ordinal);

        using var response = await fx.SendAsync(HttpMethod.Get, route, null, token);

        response.StatusCode.ShouldBe(expected);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("restricted-proposal-marker");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Resume_UuidPendingThenApproved_UsesFrozenBodyAndNeverRepeatsEffect()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var route = fx.Route + "/" + request.InvocationId + "/resume";
        using var premature = await fx.SendAsync(HttpMethod.Post, route, null, fx.Token());
        premature.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        File.ReadAllText(fx.Target).ShouldBe("original");
        var reviewer = fx.Token("human-reviewer", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        var approval = (await fx.Tool.GetApprovalAsync(request.InvocationId!.Value, reviewer)).ShouldNotBeNull();
        (await fx.Tool.DecideApprovalAsync(request.InvocationId.Value, approval.PlanDigest,
            InvocationApprovalDecision.Approve, reviewer)).Succeeded.ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("original");

        using var executed = await fx.SendAsync(HttpMethod.Post, route, null, fx.Token());

        executed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(executed)).GetProperty("outcome").GetString().ShouldBe("Succeeded");
        File.ReadAllText(fx.Target).ShouldBe("reviewed text");
        File.WriteAllText(fx.Target, "independent later marker");
        using var repeated = await fx.SendAsync(HttpMethod.Post, route, null, fx.Token());
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(repeated)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("independent later marker");
    }

    [Fact]
    public async Task Proposal_ConflictingResubmission_DoesNotReplaceOriginal()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var conflict = await fx.SendAsync(HttpMethod.Post, fx.Route, request with { RawInput = "replacement" }, fx.Token());
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var response = await fx.SendAsync(HttpMethod.Get, fx.Route + "/" + request.InvocationId + "/proposal", null,
            fx.Token(grants: ["invocation:read", "invocation:proposal:read"]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(response)).GetProperty("rawInput").GetString().ShouldBe(request.RawInput);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }
}
