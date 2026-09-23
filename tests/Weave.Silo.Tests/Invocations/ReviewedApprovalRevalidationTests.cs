using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed class ReviewedApprovalRevalidationTests
{
    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("cancelled")]
    [InlineData("substitution")]
    public async Task DecideReviewedApprovalAsync_PreviewBecomesInvalid_DoesNotRecordAnApproval(string reason)
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        var pending = await fx.WaitAsync(actor, request);
        var reviewer = fx.Approver();
        (await actor.ReviewApprovalAsync(request, reviewer)).Review.ShouldNotBeNull();
        if (reason == "expired")
            fx.Clock.Advance(TimeSpan.FromMinutes(6));
        if (reason == "revoked")
            fx.Tokens.Revoke(reviewer.TokenId);
        if (reason == "cancelled")
            reviewer = reviewer with { CancellationToken = new CancellationToken(canceled: true) };
        if (reason == "substitution")
        {
            // A different original request cannot borrow the reviewed input fingerprint.
            request = request with { Parameters = new() { ["path"] = "${secrets.other_path}" } };
            fx.Secrets.ClearReceivedCalls();
        }
        if (reason == "revoked")
            await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.DecideReviewedApprovalAsync(
                request, pending.PlanDigest, InvocationApprovalDecision.Approve, reviewer));
        else if (reason == "cancelled")
            await Should.ThrowAsync<OperationCanceledException>(() => actor.DecideReviewedApprovalAsync(
                request, pending.PlanDigest, InvocationApprovalDecision.Approve, reviewer));
        else
        {
            var result = await actor.DecideReviewedApprovalAsync(request, pending.PlanDigest,
                InvocationApprovalDecision.Approve, reviewer);
            result.Succeeded.ShouldBeFalse();
            result.ErrorCode.ShouldBe(reason == "expired" ? "approval-not-pending" : "approval-plan-conflict");
        }
        if (reason == "substitution")
            await fx.Secrets.DidNotReceive().SubstituteAsync(Arg.Any<string>());
        var restored = fx.Reopen().FindApproval("workspace", pending.InvocationId, TestContext.Current.CancellationToken).ShouldNotBeNull();
        restored.DecidedBy.ShouldBeNull();
        fx.Scalar("SELECT COUNT(*) FROM invocation_approval_decisions;").ShouldBe(0L);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }
}
