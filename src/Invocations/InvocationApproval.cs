using Weave.Shared.Ids;

namespace Weave.Invocations;

/// <summary>Durable plan evidence, not a request-body or credential cache.</summary>
/// <remarks>
/// <see cref="RequiredApprovals"/> is the quorum snapshotted when approval was requested;
/// <see cref="Approvals"/> counts distinct non-requester humans who approved this plan.
/// <see cref="DecidedBy"/> names whoever made the final state change.
/// </remarks>
public sealed record InvocationApproval(
    InvocationId InvocationId,
    string WorkspaceId,
    string Subject,
    string ToolName,
    string Operation,
    string InputDigest,
    string TargetDigest,
    string PlanDigest,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    InvocationApprovalState State,
    string? DecidedBy = null,
    string? DecisionTokenId = null,
    DateTimeOffset? DecidedAt = null,
    int RequiredApprovals = 1,
    int Approvals = 0)
{
    public InvocationApproval At(DateTimeOffset now) =>
        State is InvocationApprovalState.Pending or InvocationApprovalState.Approved && now >= ExpiresAt
            ? this with { State = InvocationApprovalState.Expired }
            : this;

    public bool Matches(InvocationRecord candidate) =>
        InvocationId == candidate.InvocationId && WorkspaceId == candidate.WorkspaceId
        && Subject == candidate.Subject && ToolName == candidate.ToolName
        && Operation == candidate.Operation && InputDigest == candidate.InputDigest
        && TargetDigest == candidate.ApprovalTargetDigest;
}
