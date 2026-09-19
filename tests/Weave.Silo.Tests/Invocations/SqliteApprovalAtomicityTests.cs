using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations;
using Weave.Shared.Ids;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class SqliteApprovalAtomicityTests
{
    [Fact]
    public async Task TryStart_ConcurrentApprovedResumes_DispatchesExactlyOneAttempt()
    {
        using var fx = new ApprovalScenario();
        var first = Candidate(fx);
        Approve(fx, first);
        var second = first with { Attempt = first.Attempt with { AttemptId = InvocationAttemptId.From(Guid.NewGuid().ToString("N")) } };
        var journal = fx.Reopen();
        var effects = 0;
        Task<ToolResult> DispatchAsync()
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ToolResult { Success = true });
        }
        var results = await Task.WhenAll(
            Task.Run(() => new InvocationExecution(fx.Journal, fx.Clock, NullLogger.Instance).ExecuteAsync(first,
                () => Task.CompletedTask, DispatchAsync, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(() => new InvocationExecution(journal, fx.Clock, NullLogger.Instance).ExecuteAsync(second,
                () => Task.CompletedTask, DispatchAsync, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        effects.ShouldBe(1);
        results.Count(r => r.IsReplay).ShouldBe(1);
        results[0].AttemptId.ShouldBe(results[1].AttemptId);
        fx.Scalar("SELECT COUNT(*) FROM invocations;").ShouldBe(1L);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1L);
        fx.Journal.FindApproval("workspace", first.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Consumed);
    }

    [Fact]
    public async Task TryStart_AttemptInsertFails_RollsBackApprovalConsumption()
    {
        using var fx = new ApprovalScenario();
        var candidate = Candidate(fx);
        Approve(fx, candidate);
        fx.ExecuteSql("CREATE TRIGGER reject_approved_attempt BEFORE INSERT ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private disk detail'); END;");
        var effects = 0;
        var result = await new InvocationExecution(fx.Journal, fx.Clock, NullLogger.Instance).ExecuteAsync(candidate,
            () => Task.CompletedTask, () =>
            {
                effects++;
                return Task.FromResult(new ToolResult { Success = true });
            }, TestContext.Current.CancellationToken);
        effects.ShouldBe(0);
        result.ErrorCode.ShouldBe("journal-write-failed");
        result.Error.ShouldNotBeNull().ShouldNotContain("private disk detail");
        fx.Reopen().FindApproval("workspace", candidate.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().State.ShouldBe(InvocationApprovalState.Approved);
        fx.Scalar("SELECT COUNT(*) FROM invocations;").ShouldBe(0L);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
    }

    [Fact]
    public void DecideApproval_DecisionHistoryInsertFails_RollsBackTheDecision()
    {
        using var fx = new ApprovalScenario();
        var candidate = Candidate(fx);
        var approval = fx.Journal.TryStart(candidate, TestContext.Current.CancellationToken).Approval.ShouldNotBeNull();
        fx.ExecuteSql("CREATE TRIGGER reject_decision BEFORE INSERT ON invocation_approval_decisions BEGIN SELECT RAISE(ABORT, 'cannot record decision'); END;");
        Should.Throw<SqliteException>(() => fx.Journal.DecideApproval("workspace", candidate.InvocationId, approval.PlanDigest,
            "approver", "decision-token", InvocationApprovalDecision.Approve, fx.Clock.GetUtcNow(), TestContext.Current.CancellationToken));
        var restored = fx.Reopen().FindApproval("workspace", candidate.InvocationId, TestContext.Current.CancellationToken).ShouldNotBeNull();
        restored.State.ShouldBe(InvocationApprovalState.Pending);
        restored.DecidedBy.ShouldBeNull();
        fx.Scalar("SELECT COUNT(*) FROM invocation_approval_decisions;").ShouldBe(0L);
    }

    [Fact]
    public async Task DecideApproval_ConcurrentOppositeDecisions_OnlyOneCommits()
    {
        using var fx = new ApprovalScenario();
        var candidate = Candidate(fx);
        var approval = fx.Journal.TryStart(candidate, TestContext.Current.CancellationToken).Approval.ShouldNotBeNull();
        var other = fx.Reopen();
        var results = await Task.WhenAll(
            Task.Run(() => fx.Journal.DecideApproval("workspace", candidate.InvocationId, approval.PlanDigest,
                "approver-a", "token-a", InvocationApprovalDecision.Approve, fx.Clock.GetUtcNow(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(() => other.DecideApproval("workspace", candidate.InvocationId, approval.PlanDigest,
                "approver-b", "token-b", InvocationApprovalDecision.Reject, fx.Clock.GetUtcNow(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        results.Count(r => r.Succeeded).ShouldBe(1);
        results.Count(r => r.ErrorCode == "approval-already-decided").ShouldBe(1);
        fx.Scalar("SELECT COUNT(*) FROM invocation_approval_decisions;").ShouldBe(1L);
    }

    [Fact]
    public async Task ExecuteAsync_ApprovalExpiresDuringPostClaimRevalidation_DoesNotDispatch()
    {
        using var fx = new ApprovalScenario();
        var candidate = Candidate(fx);
        Approve(fx, candidate);
        var effects = 0;
        var result = await new InvocationExecution(fx.Journal, fx.Clock, NullLogger.Instance).ExecuteAsync(candidate, () =>
        {
            fx.Clock.Advance(TimeSpan.FromMinutes(5));
            return Task.CompletedTask;
        }, () =>
        {
            effects++;
            return Task.FromResult(new ToolResult { Success = true });
        }, TestContext.Current.CancellationToken);
        effects.ShouldBe(0);
        result.ErrorCode.ShouldBe("approval-expired");
        result.Outcome.ShouldBe(InvocationOutcome.Denied);
        result.OutcomeRecorded.ShouldBeTrue();
        fx.Reopen().Find("workspace", candidate.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().Attempt.Outcome.ShouldBe(InvocationOutcome.Denied);
    }

    [Fact]
    public void TryStart_ApprovalRequiredWithoutAdapterTarget_FailsClosed()
    {
        using var fx = new ApprovalScenario();
        var candidate = Candidate(fx) with { ApprovalTargetDigest = null };
        var result = fx.Journal.TryStart(candidate, TestContext.Current.CancellationToken);
        result.Created.ShouldBeFalse();
        result.BlockingReason.ShouldBe("approval-target-unsupported");
        fx.Scalar("SELECT COUNT(*) FROM invocation_approvals;").ShouldBe(0L);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
    }

    private static InvocationRecord Candidate(ApprovalScenario fx)
    {
        var now = fx.Clock.GetUtcNow();
        return new InvocationRecord(InvocationId.From(Guid.NewGuid().ToString("N")), "workspace", "writer", "files",
            "write_file", "synthetic-input-digest", "request-token", "tool:files:invoke:write_file", now,
            new InvocationAttempt(InvocationAttemptId.From(Guid.NewGuid().ToString("N")), now,
                InvocationOutcome.OutcomeUnknown, null, TimeSpan.Zero)) { ApprovalTargetDigest = "synthetic-target-digest" };
    }

    private static void Approve(ApprovalScenario fx, InvocationRecord candidate)
    {
        var pending = fx.Journal.TryStart(candidate, TestContext.Current.CancellationToken);
        pending.Created.ShouldBeFalse();
        var approval = pending.Approval.ShouldNotBeNull();
        fx.Journal.DecideApproval("workspace", candidate.InvocationId, approval.PlanDigest, "approver", "decision-token",
            InvocationApprovalDecision.Approve, fx.Clock.GetUtcNow(), TestContext.Current.CancellationToken).Succeeded.ShouldBeTrue();
    }
}
