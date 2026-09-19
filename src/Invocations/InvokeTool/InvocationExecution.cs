using Microsoft.Extensions.Logging;
using Weave.Tools.Tool;

namespace Weave.Invocations;

/// <summary>One claimed attempt. No connector retries, background replay or response-body cache.</summary>
public sealed class InvocationExecution(IInvocationJournal journal, TimeProvider timeProvider, ILogger logger)
{
    public async Task<ToolResult> ExecuteAsync(InvocationRecord candidate, CancellationToken cancellationToken,
        Func<Task> revalidate, Func<Task<ToolResult>> dispatch)
    {
        InvocationClaim claim;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            claim = journal.TryStart(candidate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogRecordingFailure(candidate, error);
            return Failure(candidate, InvocationOutcome.NotDispatched, "journal-write-failed",
                "Execution did not start because its durable intent could not be recorded.");
        }

        var record = claim.Record;
        if (record.InvocationId != candidate.InvocationId || record.WorkspaceId != candidate.WorkspaceId
            || record.Subject != candidate.Subject || record.ToolName != candidate.ToolName
            || record.Operation != candidate.Operation || record.InputDigest != candidate.InputDigest)
            return Failure(candidate, InvocationOutcome.NotDispatched, "invocation-id-conflict",
                "This invocation ID is already in use for a different request.");
        if (!claim.Created)
            return Replay(record);

        try
        {
            // Journal I/O is a boundary too: authority may expire/revoke while it waits.
            await revalidate();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            RecordOutcome(record, InvocationOutcome.Cancelled, TimeSpan.Zero);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            RecordOutcome(record, InvocationOutcome.Denied, TimeSpan.Zero);
            throw;
        }
        catch
        {
            RecordOutcome(record, InvocationOutcome.NotDispatched, TimeSpan.Zero);
            throw;
        }

        var started = timeProvider.GetTimestamp();
        ToolResult result;
        try
        {
            result = await dispatch();
        }
        catch (Exception error)
        {
            var duration = timeProvider.GetElapsedTime(started);
            logger.LogWarning("Invocation {InvocationId} has an unconfirmed dispatch outcome ({ErrorType})",
                record.InvocationId, error.GetType().Name);
            var recorded = RecordOutcome(record, InvocationOutcome.OutcomeUnknown, duration);
            return Failure(record, InvocationOutcome.OutcomeUnknown, "outcome-unknown",
                "The tool outcome is unconfirmed. Query this invocation; do not retry with a new ID.")
                with { Duration = duration, OutcomeRecorded = recorded };
        }

        // Older adapters flatten transport failures into Success=false. Do not invent
        // certainty from that flag. Only an explicit, adapter-confirmed Failed is final.
        var outcome = result.Success ? InvocationOutcome.Succeeded
            : result.Outcome == InvocationOutcome.Failed ? InvocationOutcome.Failed
            : InvocationOutcome.OutcomeUnknown;
        var elapsed = timeProvider.GetElapsedTime(started);
        if (!RecordOutcome(record, outcome, elapsed))
            return Failure(record, InvocationOutcome.OutcomeUnknown, "outcome-not-recorded",
                "The tool returned, but its outcome could not be durably recorded. Query this ID; do not retry.")
                with { Duration = elapsed };

        return result with
        {
            InvocationId = record.InvocationId,
            AttemptId = record.Attempt.AttemptId,
            Outcome = outcome,
            OutcomeRecorded = true,
            IsReplay = false
        };
    }

    private bool RecordOutcome(InvocationRecord record, InvocationOutcome outcome, TimeSpan duration)
    {
        try
        {
            if (journal.Complete(record.WorkspaceId, record.InvocationId, record.Attempt.AttemptId,
                outcome, timeProvider.GetUtcNow(), duration))
                return true;
            logger.LogWarning("Invocation {InvocationId} outcome was not recorded because the attempt no longer matched",
                record.InvocationId);
        }
        catch (Exception error)
        {
            LogRecordingFailure(record, error);
        }
        return false;
    }

    private void LogRecordingFailure(InvocationRecord record, Exception error) =>
        logger.LogWarning("Invocation {InvocationId} journal write failed ({ErrorType})",
            record.InvocationId, error.GetType().Name);

    private static ToolResult Replay(InvocationRecord record) => new()
    {
        ToolName = record.ToolName,
        InvocationId = record.InvocationId,
        AttemptId = record.Attempt.AttemptId,
        Success = record.Attempt.Outcome == InvocationOutcome.Succeeded,
        Outcome = record.Attempt.Outcome,
        OutcomeRecorded = record.Attempt.CompletedAt is not null,
        IsReplay = true,
        Duration = record.Attempt.Duration,
        Error = record.Attempt.Outcome == InvocationOutcome.Succeeded ? null
            : "This invocation was already claimed. Its stored outcome does not permit replay.",
        ErrorCode = record.Attempt.Outcome == InvocationOutcome.Succeeded ? null : "invocation-already-claimed"
    };

    private static ToolResult Failure(InvocationRecord record, InvocationOutcome outcome, string code, string message) => new()
    {
        ToolName = record.ToolName,
        InvocationId = record.InvocationId,
        AttemptId = record.Attempt.AttemptId,
        Success = false,
        Outcome = outcome,
        ErrorCode = code,
        Error = message
    };
}
