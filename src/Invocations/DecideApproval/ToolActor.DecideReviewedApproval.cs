using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Tools.Tool;

public sealed partial class ToolActor
{
    public async Task<InvocationApprovalDecisionResult> DecideReviewedApprovalAsync(ToolInvocation invocation,
        string planDigest, InvocationApprovalDecision decision, CapabilityToken token)
    {
        EnsureApprovalIdentity();
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        await authorizer.AuthorizeAsync(token, "approval:decide", _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        if (decision is not (InvocationApprovalDecision.Approve or InvocationApprovalDecision.Reject)
            || string.IsNullOrWhiteSpace(planDigest) || planDigest.Length > 128)
            return new(null, "invalid-approval-decision");

        // Reverify the retained content and live target; a client-supplied preview is not evidence.
        var checkedPlan = await ReviewApprovalAsync(invocation, token);
        if (checkedPlan.Review is not { } review)
            return new(null, checkedPlan.ErrorCode);
        if (!string.Equals(review.PlanDigest, planDigest, StringComparison.Ordinal))
            return new(null, "approval-plan-conflict");
        token.CancellationToken.ThrowIfCancellationRequested();

        // Decide on the verified snapshot's ID, never on a mutable request after an await.
        // This records a decision only. Dispatch remains the original caller's separate operation.
        return await DecideApprovalAsync(review.InvocationId, review.PlanDigest, decision, token);
    }
}
