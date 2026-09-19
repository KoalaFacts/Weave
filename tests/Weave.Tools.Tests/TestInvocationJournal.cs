using Weave.Invocations;
using Weave.Shared.Ids;

namespace Weave.Tools.Tests;

/// <summary>Unit-test double only. Real persistence is exercised in the Host test project.</summary>
internal sealed class TestInvocationJournal : IInvocationJournal
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Workspace, InvocationId Id), InvocationRecord> _records = [];

    public InvocationClaim TryStart(InvocationRecord candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (candidate.WorkspaceId, candidate.InvocationId);
            if (_records.TryGetValue(key, out var existing))
                return new InvocationClaim(false, existing);
            _records.Add(key, candidate);
            return new InvocationClaim(true, candidate);
        }
    }

    public InvocationRecord? Find(string workspaceId, InvocationId invocationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return _records.GetValueOrDefault((workspaceId, invocationId));
    }

    public bool Complete(string workspaceId, InvocationId invocationId, InvocationAttemptId attemptId,
        InvocationOutcome outcome, DateTimeOffset completedAt, TimeSpan duration)
    {
        lock (_gate)
        {
            var key = (workspaceId, invocationId);
            if (!_records.TryGetValue(key, out var record)
                || record.Attempt.AttemptId != attemptId || record.Attempt.CompletedAt is not null)
                return false;
            _records[key] = record with
            {
                Attempt = record.Attempt with { Outcome = outcome, CompletedAt = completedAt, Duration = duration }
            };
            return true;
        }
    }
}
