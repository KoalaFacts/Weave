namespace Weave.Invocations;

public sealed record InvocationApprovalDecisionResult(InvocationApproval? Approval, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}
