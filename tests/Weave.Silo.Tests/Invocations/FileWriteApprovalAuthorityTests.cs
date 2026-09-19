using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Silo.Tests.Invocations;

public sealed class FileWriteApprovalAuthorityTests
{
    [Fact]
    public async Task DecideApprovalAsync_RequesterCannotSelfApprove()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        var self = fx.Mint("writer", "approval:decide", "tool:files:invoke:write_file");
        var result = await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, self);
        result.Applied.ShouldBeFalse();
        result.ErrorCode.ShouldBe("approval-subject-not-permitted");
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }

    [Theory]
    [InlineData("missing-review-grant")]
    [InlineData("missing-write-grant")]
    [InlineData("revoked")]
    [InlineData("tampered")]
    [InlineData("foreign-workspace")]
    public async Task DecideApprovalAsync_InvalidReviewerAuthority_DoesNotApprove(string condition)
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        var token = condition switch
        {
            "missing-review-grant" => fx.Mint("reviewer", "tool:files:invoke:write_file"),
            "missing-write-grant" => fx.Mint("reviewer", "approval:decide"),
            "tampered" => fx.Reviewer with { Signature = "tampered" },
            "foreign-workspace" => fx.Tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "another-workspace",
                IssuedTo = "reviewer",
                Grants = ["*"],
                Lifetime = TimeSpan.FromHours(1)
            }),
            _ => fx.Reviewer
        };
        if (condition == "revoked")
            fx.Tokens.Revoke(token.TokenId);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Tool.DecideApprovalAsync(
            approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, token));
        (await fx.Tool.GetApprovalAsync(approval.Plan.InvocationId, fx.Writer)).ShouldNotBeNull().State.ShouldBe(ApprovalState.Pending);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }

    [Fact]
    public async Task GetApprovalAsync_UnrelatedReader_DoesNotDiscloseNote()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Tool.GetApprovalAsync(
            approval.Plan.InvocationId, fx.Mint("other", "approval:read")));
    }

    [Fact]
    public async Task DecideApprovalAsync_ChangedDigest_DoesNotApprove()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        var result = await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, "wrong-digest", ApprovalDecision.Approve, fx.Reviewer);
        result.ErrorCode.ShouldBe("approval-plan-conflict");
        result.Applied.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ApprovalDecision.Reject, ApprovalState.Rejected)]
    [InlineData(ApprovalDecision.Cancel, ApprovalState.Cancelled)]
    public async Task ResumeApprovedAsync_TerminalDecision_NeverExecutes(ApprovalDecision decision, ApprovalState state)
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        var token = decision == ApprovalDecision.Cancel ? fx.Writer : fx.Reviewer;
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, decision, token)).Applied.ShouldBeTrue();
        var result = await fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer);
        result.Success.ShouldBeFalse();
        result.ApprovalState.ShouldBe(state);
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeFalse();
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Fact]
    public async Task DecideApprovalAsync_CancelAfterApproval_PreservesReviewerEvidence()
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Cancel, fx.Writer)).Applied.ShouldBeTrue();
        var result = (await fx.Tool.GetApprovalAsync(approval.Plan.InvocationId, fx.Reviewer)).ShouldNotBeNull();
        result.State.ShouldBe(ApprovalState.Cancelled);
        result.DecidedBy.ShouldBe("reviewer");
        result.CancelledBy.ShouldBe("writer");
        (await fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer)).Success.ShouldBeFalse();
        File.ReadAllText(fx.FilePath).ShouldBe("original");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeApprovedAsync_ExpiredPendingOrApprovedPlan_NeverExecutes(bool approve)
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        if (approve)
            (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, fx.Reviewer)).Applied.ShouldBeTrue();
        fx.Advance(TimeSpan.FromMinutes(11));
        var result = await fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, fx.Writer);
        result.ApprovalState.ShouldBe(ApprovalState.Expired);
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeApprovedAsync_RequesterOrReviewerRevoked_BlocksExecution(bool revokeReviewer)
    {
        await using var fx = await FileWriteApprovalScenario.StartAsync();
        var approval = await fx.ProposeAsync(FileWriteApprovalScenario.Request());
        var reviewer = fx.Reviewer;
        var writer = fx.Writer;
        (await fx.Tool.DecideApprovalAsync(approval.Plan.InvocationId, approval.PlanDigest, ApprovalDecision.Approve, reviewer)).Applied.ShouldBeTrue();
        fx.Tokens.Revoke(revokeReviewer ? reviewer.TokenId : writer.TokenId);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.Tool.ResumeApprovedAsync(approval.Plan.InvocationId, writer));
        File.ReadAllText(fx.FilePath).ShouldBe("original");
        fx.Sql("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0);
    }
}
