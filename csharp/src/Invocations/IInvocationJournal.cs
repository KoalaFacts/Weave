using Weave.Shared.Ids;

namespace Weave.Invocations;

/// <summary>
/// Mandatory durable dispatch boundary. Implementations atomically insert the intent,
/// authorization evidence and attempt, or return the existing workspace-scoped record.
/// No production in-memory fallback is permitted. No transaction spans a connector call.
/// </summary>
public interface IInvocationJournal
{
    InvocationClaim TryStart(InvocationRecord candidate, CancellationToken cancellationToken);
    InvocationRecord? Find(string workspaceId, InvocationId invocationId, CancellationToken cancellationToken);

    /// <summary>
    /// Confirm an outcome once, conditional on the exact attempt and no prior completion.
    /// Uses bounded, service-owned I/O, not the disconnected caller's cancellation token.
    /// False or an exception means the caller must not claim a durably confirmed outcome.
    /// </summary>
    bool Complete(string workspaceId, InvocationId invocationId, InvocationAttemptId attemptId,
        InvocationOutcome outcome, DateTimeOffset completedAt, TimeSpan duration);
}
