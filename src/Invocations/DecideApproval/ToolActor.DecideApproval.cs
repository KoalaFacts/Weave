using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Tools.Tool;

public sealed partial class ToolActor
{
    public async Task<InvocationApprovalDecisionResult> DecideApprovalAsync(InvocationId invocationId,
        string planDigest, InvocationApprovalDecision decision, CapabilityToken token)
    {
        EnsureApprovalIdentity();
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        var grant = decision == InvocationApprovalDecision.Cancel ? "invocation:cancel" : "approval:decide";
        token.CancellationToken.ThrowIfCancellationRequested();
        await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);
        if (!Enum.IsDefined(decision) || string.IsNullOrWhiteSpace(planDigest) || planDigest.Length > 128
            || !Guid.TryParseExact(invocationId.ToString(), "N", out var guid) || guid == Guid.Empty)
            return new(null, "invalid-approval-decision");
        invocationId = InvocationId.From(guid.ToString("N"));
        var approvals = journal as IInvocationApprovalJournal
            ?? throw new InvalidOperationException("The configured journal does not support approvals.");
        var approval = approvals.FindApproval(_identity.WorkspaceId, invocationId, token.CancellationToken);
        await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        if (approval is null || !string.Equals(approval.ToolName, _identity.ToolName, StringComparison.Ordinal))
            return new(null, "approval-not-found");
        if (decision == InvocationApprovalDecision.Cancel)
        {
            if (!string.Equals(approval.Subject, token.IssuedTo, StringComparison.Ordinal))
                return new(null, "approval-subject-denied");
        }
        else
        {
            await authorizer.AuthorizeAsync(token, ToolCapability.Approve(_identity.ToolName, approval.Operation), _identity.WorkspaceId);
            if (string.Equals(approval.Subject, token.IssuedTo, StringComparison.Ordinal))
                return new(null, "approval-subject-denied");
        }
        token.CancellationToken.ThrowIfCancellationRequested();
        return approvals.DecideApproval(_identity.WorkspaceId, invocationId, planDigest, token.IssuedTo,
            token.TokenId, decision, timeProvider.GetUtcNow(), token.CancellationToken);
    }
}
