using System.Net;
using System.Net.Http.Headers;
using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task Review_EquivalentReconnectAndCanonicalSelectors_ReturnsSamePendingPlan()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reviewer = fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        var original = (await fx.Tool.GetApprovalAsync(request.InvocationId!.Value, reviewer)).ShouldNotBeNull();
        await fx.Tool.DisconnectAsync();
        await fx.ConnectAsync();
        var equivalent = request with
        {
            InvocationId = InvocationId.From(request.InvocationId.Value.ToString().ToUpperInvariant()),
            Method = "WRITE_FILE"
        };
        for (var i = 0; i < 2; i++)
        {
            using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, equivalent), equivalent, reviewer);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await JsonAsync(response);
            body.GetProperty("planDigest").GetString().ShouldBe(original.PlanDigest);
            body.GetProperty("invocationId").GetString().ShouldBe(request.InvocationId.Value.ToString());
            body.GetProperty("operation").GetString().ShouldBe("write_file");
        }
        (await fx.Tool.GetApprovalAsync(request.InvocationId.Value, reviewer)).ShouldBe(original);
        (await fx.Tool.GetInvocationAsync(request.InvocationId.Value, fx.Token())).ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData(InvocationApprovalDecision.Approve)]
    [InlineData(InvocationApprovalDecision.Reject)]
    [InlineData(InvocationApprovalDecision.Cancel)]
    public async Task Review_AlreadyDecidedPlan_DoesNotPresentItAsPending(InvocationApprovalDecision decision)
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reviewer = fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        var approval = (await fx.Tool.GetApprovalAsync(request.InvocationId!.Value, reviewer)).ShouldNotBeNull();
        var decider = decision == InvocationApprovalDecision.Cancel
            ? fx.Token(grants: ["invocation:cancel"]) : reviewer;
        (await fx.Tool.DecideApprovalAsync(request.InvocationId.Value, approval.PlanDigest, decision, decider))
            .Succeeded.ShouldBeTrue();
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request, reviewer);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(response)).GetProperty("errorCode").GetString().ShouldBe("approval-not-pending");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Review_DisconnectedTarget_CannotClaimVerifiedTarget()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await fx.Tool.DisconnectAsync();
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request,
            fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(response)).GetProperty("errorCode").GetString().ShouldBe("approval-review-unavailable");
    }

    [Fact]
    public async Task Review_RouteAndBodyIdsDiffer_RejectsBeforeLookingUpAnotherPlan()
    {
        await using var fx = new Fixture(requireApproval: true);
        var request = Request();
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, Request()), request,
            fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("errorCode").GetString().ShouldBe("invocation-id-mismatch");
    }

    [Fact]
    public async Task Review_MissingApproval_ReturnsNotFoundWithoutTargetDisclosure()
    {
        await using var fx = new Fixture(requireApproval: true);
        await fx.ConnectAsync();
        var request = Request();
        using var response = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request,
            fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(Path.GetDirectoryName(fx.Target)!);
        body.ShouldNotContain("reviewed text");
    }

    [Fact]
    public async Task Review_GlobalAuthenticationEnabled_StillRequiresBothCredentials()
    {
        await using var fx = new Fixture(requireApproval: true, globalAuthentication: true);
        await fx.ConnectAsync();
        fx.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fx.GlobalApiSecret);
        var request = Request();
        using var pending = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reviewer = fx.Token("operator", ["invocation:read", "approval:decide", "tool:files:approve:write_file"]);
        fx.Client.DefaultRequestHeaders.Authorization = null;
        using var denied = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request, reviewer);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fx.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fx.GlobalApiSecret);
        using var invalidCapability = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request,
            reviewer with { Signature = "invalid" });
        invalidCapability.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var allowed = await fx.SendAsync(HttpMethod.Post, ReviewRoute(fx, request), request, reviewer);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }
}
