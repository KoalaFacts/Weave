using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations;
using Weave.Shared.Ids;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class InvocationReplayAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ExistingRecordStillRequiresFreshAuthority_LeavesOriginalOutcomeUnchanged(bool cancelled)
    {
        var journal = new TestInvocationJournal();
        var now = TimeProvider.System.GetUtcNow();
        var candidate = new InvocationRecord(InvocationId.From(Guid.NewGuid().ToString("N")), "ws", "writer", "files",
            "write_file", "test-digest", "test-token", "tool:files:invoke:write_file", now,
            new InvocationAttempt(InvocationAttemptId.From(Guid.NewGuid().ToString("N")), now,
                InvocationOutcome.OutcomeUnknown, null, TimeSpan.Zero));
        journal.TryStart(candidate, TestContext.Current.CancellationToken);
        journal.Complete("ws", candidate.InvocationId, candidate.Attempt.AttemptId,
            InvocationOutcome.Succeeded, now, TimeSpan.Zero).ShouldBeTrue();
        var calls = 0;
        var execution = new InvocationExecution(journal, TimeProvider.System, NullLogger.Instance);
        Task RevalidateAsync()
        {
            if (cancelled)
                throw new OperationCanceledException();
            throw new UnauthorizedAccessException();
        }
        Task<ToolResult> DispatchAsync()
        {
            calls++;
            return Task.FromResult(new ToolResult { Success = true });
        }

        var exception = await Record.ExceptionAsync(() => execution.ExecuteAsync(candidate,
            RevalidateAsync, DispatchAsync, TestContext.Current.CancellationToken));

        if (cancelled)
            exception.ShouldBeOfType<OperationCanceledException>();
        else
            exception.ShouldBeOfType<UnauthorizedAccessException>();
        calls.ShouldBe(0);
        journal.Find("ws", candidate.InvocationId, TestContext.Current.CancellationToken)
            .ShouldNotBeNull().Attempt.Outcome.ShouldBe(InvocationOutcome.Succeeded);
    }
}
