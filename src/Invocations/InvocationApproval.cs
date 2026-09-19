using Weave.Shared.Ids;

namespace Weave.Invocations;

/// <summary>Durable plan evidence, not a request-body or credential cache.</summary>
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
    DateTimeOffset? DecidedAt = null)
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
