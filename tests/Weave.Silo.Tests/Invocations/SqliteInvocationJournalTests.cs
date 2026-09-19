using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Security.Sqlite;
using Weave.Shared.Ids;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.Invocations;

public sealed class SqliteInvocationJournalTests
{
    [Fact]
    public async Task TryStart_AttemptInsertFails_RollsBackIntentAndBlocksEffect()
    {
        using var fx = new Fixture();
        fx.ExecuteSql("CREATE TRIGGER reject_attempt BEFORE INSERT ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private database detail'); END;");
        var record = Candidate();
        var result = await fx.Execution().ExecuteAsync(record, () => Task.CompletedTask, fx.WriteAsync,
            TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse();
        result.Outcome.ShouldBe(InvocationOutcome.NotDispatched);
        result.ErrorCode.ShouldBe("journal-write-failed");
        result.Error.ShouldNotContain("private database detail");
        File.Exists(fx.EffectPath).ShouldBeFalse();
        fx.Scalar("SELECT COUNT(*) FROM invocations;").ShouldBe(0L);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(0L);
    }

    [Fact]
    public async Task Complete_WriteFails_ReturnsUnknownAndNeverRepeatsEffect()
    {
        using var fx = new Fixture();
        fx.ExecuteSql("CREATE TRIGGER reject_completion BEFORE UPDATE ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private completion detail'); END;");
        var record = Candidate();
        var result = await fx.Execution().ExecuteAsync(record, () => Task.CompletedTask, fx.WriteAsync,
            TestContext.Current.CancellationToken);
        File.ReadAllText(fx.EffectPath).ShouldBe("one effect");
        result.Success.ShouldBeFalse();
        result.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
        result.OutcomeRecorded.ShouldBeFalse();
        result.ErrorCode.ShouldBe("outcome-not-recorded");
        result.Output.ShouldBeEmpty();

        var restarted = fx.Reopen();
        var stored = restarted.Find(record.WorkspaceId, record.InvocationId, TestContext.Current.CancellationToken).ShouldNotBeNull();
        stored.Attempt.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
        stored.Attempt.CompletedAt.ShouldBeNull();
        var replay = await fx.Execution(restarted).ExecuteAsync(record, () => Task.CompletedTask, fx.WriteAsync,
            TestContext.Current.CancellationToken);
        replay.IsReplay.ShouldBeTrue();
        replay.Success.ShouldBeFalse();
        File.ReadAllText(fx.EffectPath).ShouldBe("one effect");
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("transport")]
    public async Task ExecuteAsync_EffectThenLostResponse_RecordsUnknownWithoutReplay(string failure)
    {
        using var fx = new Fixture();
        var record = Candidate();
        async Task<ToolResult> LoseResponseAsync()
        {
            await fx.WriteAsync();
            throw failure switch
            {
                "timeout" => new TimeoutException("private upstream response"),
                "cancelled" => new OperationCanceledException("private upstream response"),
                _ => new IOException("private upstream response")
            };
        }
        var result = await fx.Execution().ExecuteAsync(record, () => Task.CompletedTask, LoseResponseAsync,
            TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
        result.OutcomeRecorded.ShouldBeTrue();
        result.Error.ShouldNotContain("private upstream response");
        var reopened = fx.Reopen();
        var stored = reopened.Find(record.WorkspaceId, record.InvocationId, TestContext.Current.CancellationToken).ShouldNotBeNull();
        stored.Attempt.CompletedAt.ShouldNotBeNull();
        stored.Attempt.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
        var replay = await fx.Execution(reopened).ExecuteAsync(record, () => Task.CompletedTask, fx.WriteAsync,
            TestContext.Current.CancellationToken);
        replay.IsReplay.ShouldBeTrue();
        File.ReadAllText(fx.EffectPath).ShouldBe("one effect");
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancelledAfterEffect_StillRecordsConfirmedOutcome()
    {
        using var fx = new Fixture();
        using var caller = new CancellationTokenSource();
        var record = Candidate();
        var result = await fx.Execution().ExecuteAsync(record, () => Task.CompletedTask, async () =>
        {
            var response = await fx.WriteAsync();
            await caller.CancelAsync();
            return response;
        }, caller.Token);
        result.Success.ShouldBeTrue();
        result.OutcomeRecorded.ShouldBeTrue();
        var stored = fx.Reopen().Find(record.WorkspaceId, record.InvocationId, TestContext.Current.CancellationToken).ShouldNotBeNull();
        stored.Attempt.Outcome.ShouldBe(InvocationOutcome.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_AuthorityOrCancellationChangesAfterClaim_BlocksEffect(bool cancelled)
    {
        using var fx = new Fixture();
        var record = Candidate();
        Task RevalidateAsync()
        {
            if (cancelled)
                throw new OperationCanceledException();
            throw new UnauthorizedAccessException();
        }
        var exception = await Record.ExceptionAsync(() => fx.Execution().ExecuteAsync(record,
            RevalidateAsync, fx.WriteAsync, TestContext.Current.CancellationToken));
        if (cancelled)
            exception.ShouldBeOfType<OperationCanceledException>();
        else
            exception.ShouldBeOfType<UnauthorizedAccessException>();
        File.Exists(fx.EffectPath).ShouldBeFalse();
        var stored = fx.Reopen().Find(record.WorkspaceId, record.InvocationId, TestContext.Current.CancellationToken).ShouldNotBeNull();
        stored.Attempt.Outcome.ShouldBe(cancelled ? InvocationOutcome.Cancelled : InvocationOutcome.Denied);
        stored.Attempt.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task TryStart_ConcurrentIndependentConnections_ClaimsExactlyOneAttempt()
    {
        using var fx = new Fixture();
        var second = fx.Reopen();
        var first = Candidate();
        var other = first with { Attempt = first.Attempt with { AttemptId = InvocationAttemptId.From(Guid.NewGuid().ToString("N")) } };
        var claims = await Task.WhenAll(
            Task.Run(() => fx.Journal.TryStart(first, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            Task.Run(() => second.TryStart(other, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        claims.Count(c => c.Created).ShouldBe(1);
        claims[0].Record.Attempt.AttemptId.ShouldBe(claims[1].Record.Attempt.AttemptId);
        fx.Scalar("SELECT COUNT(*) FROM invocations;").ShouldBe(1L);
        fx.Scalar("SELECT COUNT(*) FROM invocation_attempts;").ShouldBe(1L);
    }

    [Fact]
    public async Task ExecuteAsync_PreviousClaimWithoutCompletion_IsUnknownAfterReopenAndCannotRun()
    {
        using var fx = new Fixture();
        var record = Candidate();
        fx.Journal.TryStart(record, TestContext.Current.CancellationToken).Created.ShouldBeTrue();
        // Models the crash window after durable admission, before an observed result.
        var result = await fx.Execution(fx.Reopen()).ExecuteAsync(record, () => Task.CompletedTask, fx.WriteAsync,
            TestContext.Current.CancellationToken);
        result.IsReplay.ShouldBeTrue();
        result.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
        result.OutcomeRecorded.ShouldBeFalse();
        File.Exists(fx.EffectPath).ShouldBeFalse();
    }

    [Fact]
    public void Complete_WrongAttemptOrSecondCompletion_CannotOverwriteOutcome()
    {
        using var fx = new Fixture();
        var record = Candidate();
        fx.Journal.TryStart(record, TestContext.Current.CancellationToken);
        fx.Journal.Complete(record.WorkspaceId, record.InvocationId, InvocationAttemptId.From(Guid.NewGuid().ToString("N")),
            InvocationOutcome.Succeeded, record.CreatedAt, TimeSpan.Zero).ShouldBeFalse();
        fx.Journal.Complete(record.WorkspaceId, record.InvocationId, record.Attempt.AttemptId,
            InvocationOutcome.Succeeded, record.CreatedAt, TimeSpan.Zero).ShouldBeTrue();
        fx.Journal.Complete(record.WorkspaceId, record.InvocationId, record.Attempt.AttemptId,
            InvocationOutcome.Failed, record.CreatedAt, TimeSpan.Zero).ShouldBeFalse();
        fx.Reopen().Find(record.WorkspaceId, record.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().Attempt.Outcome.ShouldBe(InvocationOutcome.Succeeded);
    }

    [Fact]
    public void Find_SameIdInDifferentWorkspaces_DoesNotMixRecords()
    {
        using var fx = new Fixture();
        var first = Candidate();
        var second = first with
        {
            WorkspaceId = "other",
            Attempt = first.Attempt with { AttemptId = InvocationAttemptId.From(Guid.NewGuid().ToString("N")) }
        };
        fx.Journal.TryStart(first, TestContext.Current.CancellationToken).Created.ShouldBeTrue();
        fx.Journal.TryStart(second, TestContext.Current.CancellationToken).Created.ShouldBeTrue();
        fx.Journal.Find("absent", first.InvocationId, TestContext.Current.CancellationToken).ShouldBeNull();
        fx.Journal.Find("other", first.InvocationId, TestContext.Current.CancellationToken).ShouldBe(second);
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("file:journal?mode=memory")]
    [InlineData("")]
    public void Constructor_NonDurableOrAmbiguousPath_Rejects(string path)
    {
        Should.Throw<InvalidOperationException>(() => new SqliteInvocationJournal(
            Options.Create(new InvocationJournalOptions { DatabasePath = path })));
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitFailedResult_PreservesConfirmedFailure()
    {
        using var fx = new Fixture();
        var record = Candidate();
        var result = await fx.Execution().ExecuteAsync(record, () => Task.CompletedTask,
            () => Task.FromResult(new ToolResult { Success = false, Outcome = InvocationOutcome.Failed }),
            TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(InvocationOutcome.Failed);
        fx.Reopen().Find(record.WorkspaceId, record.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().Attempt.Outcome.ShouldBe(InvocationOutcome.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_UnclassifiedAdapterFailure_RemainsUnknown()
    {
        using var fx = new Fixture();
        var result = await fx.Execution().ExecuteAsync(Candidate(), () => Task.CompletedTask,
            () => Task.FromResult(new ToolResult { Success = false }), TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(InvocationOutcome.OutcomeUnknown);
        result.OutcomeRecorded.ShouldBeTrue();
    }

    private static InvocationRecord Candidate()
    {
        var now = TimeProvider.System.GetUtcNow();
        return new InvocationRecord(InvocationId.From(Guid.NewGuid().ToString("N")), "workspace", "writer", "files",
            "write_file", "test-only-digest", "test-token-id", "tool:files:invoke:write_file", now,
            new InvocationAttempt(InvocationAttemptId.From(Guid.NewGuid().ToString("N")), now,
                InvocationOutcome.OutcomeUnknown, null, TimeSpan.Zero));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"weave-journal-sql-{Guid.NewGuid():N}");
        public string EffectPath => Path.Combine(_root, "effect.txt");
        private string DatabasePath => Path.Combine(_root, "journal.db");
        public SqliteInvocationJournal Journal { get; }
        public Fixture() => Journal = Reopen();
        public SqliteInvocationJournal Reopen() => new(Options.Create(new InvocationJournalOptions { DatabasePath = DatabasePath }));
        public InvocationExecution Execution(IInvocationJournal? journal = null) =>
            new(journal ?? Journal, TimeProvider.System, NullLogger.Instance);
        public Task<ToolResult> WriteAsync()
        {
            File.AppendAllText(EffectPath, "one effect");
            return Task.FromResult(new ToolResult { Success = true, Output = "private response body" });
        }
        public void ExecuteSql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
