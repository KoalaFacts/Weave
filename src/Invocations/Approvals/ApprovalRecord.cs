namespace Weave.Invocations;

/// <summary>The plan is protected separately; audit metadata contains no clear request body.</summary>
public sealed record ApprovalRecord(
    InvocationId InvocationId,
    string WorkspaceId,
    string Subject,
    string ToolName,
    string InputDigest,
    string PlanDigest,
    string ProtectedPlan,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalState State,
    string? DecidedBy = null,
    string? DecisionTokenId = null,
    DateTimeOffset? DecidedAt = null,
    string? CancelledBy = null,
    DateTimeOffset? CancelledAt = null)
{
    public ApprovalState StateAt(DateTimeOffset now) =>
        State is ApprovalState.Pending or ApprovalState.Approved && ExpiresAt <= now
            ? ApprovalState.Expired : State;
}
