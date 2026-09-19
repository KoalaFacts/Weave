using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Security.Sqlite;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class ApprovalTransactionTests
{
    [Fact]
    public async Task TryStart_IndependentWorkers_ConsumeOneApprovalAndDispatchOnce()
    {
        using var fx = new Fixture();
        var first = fx.Open();
        var second = fx.Open();
        var approval = fx.Propose(first);
        fx.Decide(first, approval, ApprovalDecision.Approve).ShouldBeTrue();
        var candidate = fx.Candidate(approval);
        var concurrent = candidate with
        {
            Attempt = candidate.Attempt with { AttemptId = InvocationAttemptId.From(Guid.NewGuid().ToString("N")) }
        };
        var a = new InvocationExecution(first, fx.Clock, NullLogger.Instance);
        var b = new InvocationExecution(second, fx.Clock, NullLogger.Instance);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        async Task<ToolResult> RunAsync(InvocationExecution execution, InvocationRecord request)
        {
            if (Interlocked.Increment(ref waiting) == 2)
                ready.SetResult();
            await start.Task.WaitAsync(TestContext.Current.CancellationToken);
            return await execution.ExecuteAsync(request, () => Task.CompletedTask, () =>
            {
                File.AppendAllText(fx.Effect, "once");
                return Task.FromResult(new ToolResult { Success = true });
            }, TestContext.Current.CancellationToken);
        }
        var one = Task.Run(() => RunAsync(a, candidate), TestContext.Current.CancellationToken);
        var two = Task.Run(() => RunAsync(b, concurrent), TestContext.Current.CancellationToken);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        start.SetResult();
        var results = await Task.WhenAll(one, two);
        results.Count(r => !r.IsReplay && r.Success).ShouldBe(1);
        results.Count(r => r.IsReplay).ShouldBe(1);
        results[0].AttemptId.ShouldBe(results[1].AttemptId);
        File.ReadAllText(fx.Effect).ShouldBe("once");
        fx.Open().FindApproval("ws", approval.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().State.ShouldBe(ApprovalState.Consumed);
    }

    [Fact]
    public async Task TryDecideApproval_ConflictingDecisions_OnlyOneIsCommitted()
    {
        using var fx = new Fixture();
        var first = fx.Open();
        var second = fx.Open();
        var approval = fx.Propose(first);
        var results = await Task.WhenAll(
            Task.Run(() => fx.Decide(first, approval, ApprovalDecision.Approve), TestContext.Current.CancellationToken),
            Task.Run(() => fx.Decide(second, approval, ApprovalDecision.Reject), TestContext.Current.CancellationToken));
        results.Count(applied => applied).ShouldBe(1);
        var retained = fx.Open().FindApproval("ws", approval.InvocationId, TestContext.Current.CancellationToken).ShouldNotBeNull();
        retained.State.ShouldBe(results[0] ? ApprovalState.Approved : ApprovalState.Rejected);
        retained.DecidedBy.ShouldBe("reviewer");
        retained.DecisionTokenId.ShouldBe("reviewer-token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryStart_NoApprovalProof_CannotBypassPendingOrApprovedRecord(bool approved)
    {
        using var fx = new Fixture();
        var journal = fx.Open();
        var record = fx.Propose(journal);
        if (approved)
            fx.Decide(journal, record, ApprovalDecision.Approve).ShouldBeTrue();
        var request = fx.Candidate(record) with { ApprovalPlanDigest = null };
        var execution = new InvocationExecution(journal, fx.Clock, NullLogger.Instance);
        var result = await execution.ExecuteAsync(request, () => Task.CompletedTask, () =>
        {
            File.WriteAllText(fx.Effect, "bypass");
            return Task.FromResult(new ToolResult { Success = true });
        }, TestContext.Current.CancellationToken);
        result.ErrorCode.ShouldBe("approval-plan-conflict");
        result.AttemptId.ShouldBeNull();
        File.Exists(fx.Effect).ShouldBeFalse();
        journal.Find("ws", record.InvocationId, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindApproval_ObservedExpiryCannotReviveAfterClockMovesBack(bool approved)
    {
        using var fx = new Fixture();
        var journal = fx.Open();
        var approval = fx.Propose(journal);
        if (approved)
            fx.Decide(journal, approval, ApprovalDecision.Approve).ShouldBeTrue();
        fx.Clock.Now += TimeSpan.FromMinutes(11);
        journal.FindApproval("ws", approval.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().State.ShouldBe(ApprovalState.Expired);
        fx.Clock.Now -= TimeSpan.FromMinutes(11);
        fx.Open().FindApproval("ws", approval.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().State.ShouldBe(ApprovalState.Expired);
        fx.Decide(journal, approval, ApprovalDecision.Approve).ShouldBeFalse();
        journal.TryStart(fx.Candidate(approval), TestContext.Current.CancellationToken).Rejection.ShouldBe("approval-not-ready");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-approval-transactions-{Guid.NewGuid():N}");
        public TestClock Clock { get; } = new();
        public string Effect => Path.Combine(_root, "effect.txt");
        public SqliteInvocationJournal Open() => new(Options.Create(new InvocationJournalOptions
        {
            DatabasePath = Path.Combine(_root, "journal.db")
        }), Clock);
        public ApprovalRecord Propose(SqliteInvocationJournal journal)
        {
            var record = new ApprovalRecord(InvocationId.From(Guid.NewGuid().ToString("N")), "ws", "writer", "files",
                "input-digest", "plan-digest", "opaque-test-plan", Clock.Now, Clock.Now + TimeSpan.FromMinutes(10), ApprovalState.Pending);
            return journal.ProposeApproval(record, TestContext.Current.CancellationToken).ShouldNotBeNull();
        }
        public bool Decide(SqliteInvocationJournal journal, ApprovalRecord record, ApprovalDecision decision) =>
            journal.TryDecideApproval("ws", record.InvocationId, record.PlanDigest, decision, "reviewer", "reviewer-token",
                Clock.Now + TimeSpan.FromHours(1), Clock.Now, TestContext.Current.CancellationToken);
        public InvocationRecord Candidate(ApprovalRecord record) => new(record.InvocationId, "ws", "writer", "files", "write_file",
            record.InputDigest, "writer-token", "tool:files:invoke:write_file", Clock.Now,
            new InvocationAttempt(InvocationAttemptId.From(Guid.NewGuid().ToString("N")), Clock.Now,
                InvocationOutcome.OutcomeUnknown, null, TimeSpan.Zero)) { ApprovalPlanDigest = record.PlanDigest };
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = TimeProvider.System.GetUtcNow();
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
