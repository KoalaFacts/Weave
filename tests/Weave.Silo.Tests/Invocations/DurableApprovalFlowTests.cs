using Weave.Invocations;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class DurableApprovalFlowTests
{
    [Fact]
    public async Task InvokeAsync_ApprovedPlanAfterReopen_ExecutesOnceWithCurrentAuthority()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        var reopened = await fx.ConnectAsync(fx.Reopen());
        var decision = await reopened.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver());
        decision.Succeeded.ShouldBeTrue(decision.ErrorCode);
        File.ReadAllText(fx.Target).ShouldBe("original");
        (await reopened.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver())).Succeeded.ShouldBeTrue();
        fx.Scalar("SELECT COUNT(*) FROM invocation_approval_decisions;").ShouldBe(1L);

        var result = await reopened.InvokeAsync(request, fx.Writer());
        result.Success.ShouldBeTrue(result.Error);
        result.OutcomeRecorded.ShouldBeTrue();
        result.ApprovalState.ShouldBe(InvocationApprovalState.Consumed);
        File.ReadAllText(fx.Target).ShouldBe("updated");
        File.WriteAllText(fx.Target, "external change");
        (await reopened.InvokeAsync(request, fx.Writer())).IsReplay.ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("external change");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1L);
    }

    [Theory]
    [InlineData("approval:decide")]
    [InlineData("tool:files:approve:write_file")]
    [InlineData("tool:files:invoke:write_file")]
    public async Task DecideApprovalAsync_MissingDecisionAuthority_DoesNotApprove(string grant)
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var approval = await fx.WaitAsync(actor, fx.Write());
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.DecideApprovalAsync(approval.InvocationId,
            approval.PlanDigest, InvocationApprovalDecision.Approve, fx.Token("approver", grant)));
        fx.Journal.FindApproval("workspace", approval.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Pending);
    }

    [Fact]
    public async Task DecideApprovalAsync_RequestorHasApprovalGrants_StillCannotSelfApprove()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var approval = await fx.WaitAsync(actor, fx.Write());
        var result = await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Token("writer", "approval:decide", "tool:files:approve:write_file"));
        result.ErrorCode.ShouldBe("approval-subject-denied");
        fx.Scalar("SELECT COUNT(*) FROM invocation_approval_decisions;").ShouldBe(0L);
    }

    [Fact]
    public async Task GetApprovalAsync_DifferentSubjectWithoutApprovalAuthority_DoesNotExposePlan()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var approval = await fx.WaitAsync(actor, fx.Write());
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.GetApprovalAsync(approval.InvocationId,
            fx.Token("other", "invocation:read")));
        (await actor.GetApprovalAsync(approval.InvocationId, fx.Approver())).ShouldNotBeNull().PlanDigest.ShouldBe(approval.PlanDigest);
    }

    [Fact]
    public async Task DecideApprovalAsync_RejectedPlan_CannotLaterApproveOrDispatch()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Reject, fx.Approver())).Succeeded.ShouldBeTrue();
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver())).ErrorCode.ShouldBe("approval-already-decided");
        var result = await (await fx.ConnectAsync(fx.Reopen())).InvokeAsync(request, fx.Writer());
        result.ErrorCode.ShouldBe("approval-rejected");
        result.AttemptId.ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecideApprovalAsync_OwnerCancelsPendingOrApproved_PreventsDispatch(bool approveFirst)
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        if (approveFirst)
            (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
                InvocationApprovalDecision.Approve, fx.Approver())).Succeeded.ShouldBeTrue();
        var result = await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Cancel, fx.Writer());
        result.Succeeded.ShouldBeTrue(result.ErrorCode);
        (await actor.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-cancelled");
        File.ReadAllText(fx.Target).ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_PendingOrApprovedPlanExpires_DoesNotDispatch(bool approveFirst)
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        if (approveFirst)
            (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
                InvocationApprovalDecision.Approve, fx.Approver())).Succeeded.ShouldBeTrue();
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        (await actor.GetApprovalAsync(approval.InvocationId, fx.Writer())).ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Expired);
        (await actor.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-expired");
        File.ReadAllText(fx.Target).ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
    }

    [Fact]
    public async Task InvokeAsync_AuthorityRevokedAfterApproval_ApprovalDoesNotGrantExecution()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver())).Succeeded.ShouldBeTrue();
        var token = fx.Writer();
        fx.Tokens.Revoke(token.TokenId);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.InvokeAsync(request, token));
        File.ReadAllText(fx.Target).ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ApprovedInputOrTargetChanges_RejectsPlan(bool changeTarget)
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver())).Succeeded.ShouldBeTrue();
        if (changeTarget)
        {
            var other = Path.Combine(fx.Root, "other");
            Directory.CreateDirectory(other);
            actor = await fx.ConnectAsync(root: other);
        }
        else
            request = request with { RawInput = "unapproved change" };
        var result = await actor.InvokeAsync(request, fx.Writer());
        result.ErrorCode.ShouldBe("approval-plan-conflict");
        result.ApprovalPlanDigest.ShouldBeNull();
        File.ReadAllText(fx.Target).ShouldBe("original");
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
    }

    [Fact]
    public async Task InvokeAsync_SecretResolutionChanges_RejectsPreviouslyApprovedTarget()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync();
        var request = fx.Write() with { Parameters = new() { ["path"] = "${secrets.file_path}" } };
        fx.Secrets.SubstituteAsync("${secrets.file_path}").Returns("document.txt");
        var approval = await fx.WaitAsync(actor, request);
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver())).Succeeded.ShouldBeTrue();
        fx.Secrets.SubstituteAsync("${secrets.file_path}").Returns("different.txt");
        (await actor.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-plan-conflict");
        File.Exists(Path.Combine(fx.Root, "different.txt")).ShouldBeFalse();
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task InvokeAsync_RequirementRemovedAfterWaiting_DoesNotBypassStoredApproval()
    {
        using var fx = new ApprovalScenario();
        var request = fx.Write();
        await fx.WaitAsync(await fx.ConnectAsync(), request);
        var actor = await fx.ConnectAsync(fx.Reopen(requireApproval: false));
        (await actor.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-pending");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }
}
