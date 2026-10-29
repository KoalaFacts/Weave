using Weave.Invocations;

namespace Weave.Silo.Invocations;

internal sealed record ApprovalHttpStatus(
    string InvocationId, InvocationApprovalState ApprovalState, DateTimeOffset ExpiresAt,
    int RequiredApprovals, int Approvals)
{
    public static ApprovalHttpStatus From(InvocationApproval approval) => new(approval.InvocationId.ToString(),
        approval.State, approval.ExpiresAt, approval.RequiredApprovals, approval.Approvals);
}
