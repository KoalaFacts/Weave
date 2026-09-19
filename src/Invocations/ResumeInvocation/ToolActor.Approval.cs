using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Tools.Tool;

public sealed partial class ToolActor
{
    public Task<FileWriteApproval?> GetApprovalAsync(InvocationId id, CapabilityToken token)
    {
        EnsureApprovalIdentity();
        return approvals.GetAsync(_identity.WorkspaceId, _identity.ToolName, id, token);
    }

    public Task<ApprovalDecisionResult> DecideApprovalAsync(InvocationId id, string expectedDigest,
        ApprovalDecision decision, CapabilityToken token)
    {
        EnsureApprovalIdentity();
        return approvals.DecideAsync(_identity.WorkspaceId, _identity.ToolName, id, expectedDigest, decision, token);
    }

    public async Task<ToolResult> ResumeApprovedAsync(InvocationId id, CapabilityToken token)
    {
        EnsureApprovalIdentity();
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        var saved = await approvals.LoadForResumeAsync(_identity.WorkspaceId, _identity.ToolName, id, token);
        if (saved is null)
            return FileWriteApprovalService.Block(id, _identity.ToolName, "approval-not-found");
        var state = saved.Value.Record.StateAt(timeProvider.GetUtcNow());
        if (state is not (ApprovalState.Approved or ApprovalState.Consumed))
            return FileWriteApprovalService.Block(id, _identity.ToolName, "approval-not-ready") with { ApprovalState = state };
        return await InvokeCoreAsync(FileWritePlanBinding.Request(saved.Value.Plan), token, saved.Value.Record);
    }

    private void EnsureApprovalIdentity()
    {
        if (string.IsNullOrWhiteSpace(_identity.WorkspaceId) || string.IsNullOrWhiteSpace(_identity.ToolName))
            throw new InvalidOperationException("Activate or connect the tool before accessing approvals.");
    }
}
