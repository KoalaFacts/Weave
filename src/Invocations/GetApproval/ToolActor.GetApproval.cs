using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Tools.Tool;

public sealed partial class ToolActor
{
    public async Task<InvocationApproval?> GetApprovalAsync(InvocationId invocationId, CapabilityToken token)
    {
        EnsureApprovalIdentity();
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        token.CancellationToken.ThrowIfCancellationRequested();
        await authorizer.AuthorizeAsync(token, "invocation:read", _identity.WorkspaceId);
        if (!Guid.TryParseExact(invocationId.ToString(), "N", out var guid) || guid == Guid.Empty)
            return null;
        var approvals = journal as IInvocationApprovalJournal
            ?? throw new InvalidOperationException("The configured journal does not support approvals.");
        var approval = approvals.FindApproval(_identity.WorkspaceId, InvocationId.From(guid.ToString("N")), token.CancellationToken);
        await authorizer.AuthorizeAsync(token, "invocation:read", _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        if (approval is null || !string.Equals(approval.ToolName, _identity.ToolName, StringComparison.Ordinal))
            return null;
        if (!string.Equals(approval.Subject, token.IssuedTo, StringComparison.Ordinal))
        {
            await authorizer.AuthorizeAsync(token, "approval:decide", _identity.WorkspaceId);
            await authorizer.AuthorizeAsync(token, ToolCapability.Approve(_identity.ToolName, approval.Operation), _identity.WorkspaceId);
            token.CancellationToken.ThrowIfCancellationRequested();
        }
        return approval.At(timeProvider.GetUtcNow());
    }

    private void EnsureApprovalIdentity()
    {
        if (string.IsNullOrWhiteSpace(_identity.WorkspaceId) || string.IsNullOrWhiteSpace(_identity.ToolName))
            throw new InvalidOperationException("Tool identity must be established before accessing approvals.");
    }
}
