namespace Weave.Invocations;

public sealed record ApprovalDecisionResult(bool Applied, ApprovalState? State, string? ErrorCode);
