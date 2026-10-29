using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed class QuorumApprovalFlowTests
{
    [Fact]
    public async Task DecideApprovalAsync_TwoDistinctHumans_ApprovesOnlyAtQuorum()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 2));
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        approval.RequiredApprovals.ShouldBe(2);
        approval.Approvals.ShouldBe(0);

        var first = await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver("alice"));
        first.Succeeded.ShouldBeTrue(first.ErrorCode);
        first.Approval.ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Pending);
        first.Approval.Approvals.ShouldBe(1);
        (await actor.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-pending");
        File.ReadAllText(fx.Target).ShouldBe("original");

        var second = await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver("bob"));
        second.Succeeded.ShouldBeTrue(second.ErrorCode);
        second.Approval.ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Approved);
        second.Approval.Approvals.ShouldBe(2);
        second.Approval.DecidedBy.ShouldBe("bob");
        fx.Scalar("SELECT COUNT(*) FROM invocation_approval_decisions;").ShouldBe(2L);

        var result = await actor.InvokeAsync(request, fx.Writer());
        result.Success.ShouldBeTrue(result.Error);
        File.ReadAllText(fx.Target).ShouldBe("updated");
    }

    [Fact]
    public async Task DecideApprovalAsync_SameHumanApprovesTwice_CountsOnce()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 2));
        var approval = await fx.WaitAsync(actor, fx.Write());
        for (var i = 0; i < 2; i++)
        {
            var result = await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
                InvocationApprovalDecision.Approve, fx.Approver("alice"));
            result.Succeeded.ShouldBeTrue(result.ErrorCode);
            result.Approval.ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Pending);
            result.Approval.Approvals.ShouldBe(1);
        }
        fx.Scalar("SELECT COUNT(*) FROM invocation_approval_decisions;").ShouldBe(1L);
    }

    [Fact]
    public async Task DecideApprovalAsync_PartialApprovalThenReject_RejectsPlan()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 2));
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver("alice"))).Succeeded.ShouldBeTrue();
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Reject, fx.Approver("bob"))).Succeeded.ShouldBeTrue();
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver("carol"))).ErrorCode.ShouldBe("approval-already-decided");
        (await actor.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-rejected");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task DecideApprovalAsync_RequestorAmongApprovers_DoesNotCount()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 2));
        var approval = await fx.WaitAsync(actor, fx.Write());
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver("alice"))).Succeeded.ShouldBeTrue();
        (await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Token("writer", "tool:files:approve:write_file")))
            .ErrorCode.ShouldBe("approval-subject-denied");
        fx.Journal.FindApproval("workspace", approval.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Pending);
    }

    [Fact]
    public async Task DecideApprovalAsync_QuorumLoweredAfterRequest_KeepsStoredQuorumAcrossRestart()
    {
        using var fx = new ApprovalScenario();
        var request = fx.Write();
        var approval = await fx.WaitAsync(await fx.ConnectAsync(fx.Reopen(requiredApprovals: 2)), request);
        var restarted = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 1));
        var first = await restarted.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver("alice"));
        first.Approval.ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Pending);
        (await restarted.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-pending");

        var again = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 2));
        (await again.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver("bob"))).Approval.ShouldNotBeNull()
            .State.ShouldBe(InvocationApprovalState.Approved);
        (await again.InvokeAsync(request, fx.Writer())).Success.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_VersionOneJournalWithoutQuorumColumn_UpgradesExistingApprovalsToSingleApproval(bool requireExisting)
    {
        using var fx = new ApprovalScenario();
        var request = fx.Write();
        var approval = await fx.WaitAsync(await fx.ConnectAsync(), request);
        // Models a journal written before quorum approval.
        fx.ExecuteSql("ALTER TABLE invocation_approvals DROP COLUMN required_approvals; PRAGMA user_version=1;");

        var actor = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 2, requireExistingStorage: requireExisting));
        fx.Scalar("PRAGMA user_version;").ShouldBe(2L);
        var decision = await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
            InvocationApprovalDecision.Approve, fx.Approver());
        decision.Approval.ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Approved);
        decision.Approval.RequiredApprovals.ShouldBe(1);
    }

    [Fact]
    public void Open_QuorumJournalLosesQuorumColumn_FailsClosedWithoutDefaulting()
    {
        using var fx = new ApprovalScenario();
        fx.ExecuteSql("ALTER TABLE invocation_approvals DROP COLUMN required_approvals;");
        fx.Scalar("PRAGMA user_version;").ShouldBe(2L);
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => fx.Reopen(requiredApprovals: 2));
    }

    [Fact]
    public void Open_UnknownLaterSchemaVersion_FailsClosed()
    {
        // The same version gate is what makes pre-quorum binaries (which accept only
        // version 1) refuse a quorum-aware journal instead of deciding it alone.
        using var fx = new ApprovalScenario();
        fx.Scalar("PRAGMA user_version;").ShouldBe(2L);
        fx.ExecuteSql("PRAGMA user_version=3;");
        Should.Throw<InvalidOperationException>(() => fx.Reopen());
    }

    [Fact]
    public async Task DecideApprovalAsync_ThreePersonQuorum_RequiresThirdDistinctApprover()
    {
        using var fx = new ApprovalScenario();
        var actor = await fx.ConnectAsync(fx.Reopen(requiredApprovals: 3));
        var request = fx.Write();
        var approval = await fx.WaitAsync(actor, request);
        foreach (var (subject, expected) in new[] {
            ("alice", InvocationApprovalState.Pending), ("bob", InvocationApprovalState.Pending),
            ("carol", InvocationApprovalState.Approved) })
        {
            var result = await actor.DecideApprovalAsync(approval.InvocationId, approval.PlanDigest,
                InvocationApprovalDecision.Approve, fx.Approver(subject));
            result.Approval.ShouldNotBeNull().State.ShouldBe(expected);
            if (expected == InvocationApprovalState.Pending)
                (await actor.InvokeAsync(request, fx.Writer())).ErrorCode.ShouldBe("approval-pending");
        }
        (await actor.InvokeAsync(request, fx.Writer())).Success.ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("updated");
    }
}
