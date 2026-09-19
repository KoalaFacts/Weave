using Weave.Security.Tokens;

namespace Weave.Invocations;

public sealed partial class FileWriteApprovalService
{
    public async Task<ApprovalDecisionResult> DecideAsync(string workspace, string tool, InvocationId id,
        string expectedDigest, ApprovalDecision decision, CapabilityToken token)
    {
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        if (!Enum.IsDefined(decision))
            return new(false, null, "invalid-approval-decision");
        var grant = decision == ApprovalDecision.Cancel ? "approval:cancel" : "approval:decide";
        await authorizer.AuthorizeAsync(token, grant, workspace);
        token.CancellationToken.ThrowIfCancellationRequested();
        var record = store.FindApproval(workspace, Canonical(id), token.CancellationToken);
        if (record is null || record.ToolName != tool)
            return new(false, null, "approval-not-found");
        if (decision == ApprovalDecision.Cancel ? record.Subject != token.IssuedTo : record.Subject == token.IssuedTo)
            return new(false, null, "approval-subject-not-permitted");
        if (record.PlanDigest != expectedDigest)
            return new(false, null, "approval-plan-conflict");
        // A reviewer must be allowed to perform the operation as well. Approval
        // never transfers that authority to the requester.
        if (decision != ApprovalDecision.Cancel)
            await authorizer.AuthorizeAsync(token, ToolCapability.Invoke(tool, "write_file"), workspace);
        _ = Unprotect(record);
        await authorizer.AuthorizeAsync(token, grant, workspace);
        token.CancellationToken.ThrowIfCancellationRequested();
        var changed = store.TryDecideApproval(workspace, record.InvocationId, expectedDigest, decision,
            token.IssuedTo, token.TokenId, token.ExpiresAt, timeProvider.GetUtcNow(), token.CancellationToken);
        var current = store.FindApproval(workspace, record.InvocationId, token.CancellationToken);
        return new(changed, current?.StateAt(timeProvider.GetUtcNow()), changed ? null : "approval-not-pending");
    }
}
