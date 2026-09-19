namespace Weave.Invocations;

/// <summary>Authorized review projection. Deliberately exposes the exact note content, not a credential.</summary>
public sealed record FileWriteApproval(
    FileWriteApprovalPlan Plan,
    string PlanDigest,
    ApprovalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? CancelledBy,
    DateTimeOffset? CancelledAt);
