using Weave.Security.Tokens;
using Weave.Tools.Tool;

namespace Weave.Invocations;

/// <summary>Authorize metadata and the exact operation before loading proposal contents.</summary>
public sealed class InvocationProposalReader(IInvocationJournal journal, ICapabilityAuthorizer authorizer)
{
    public Task<InvocationProposalReadResult> ReadOwnedAsync(string workspaceId, string toolName,
        InvocationId invocationId, CapabilityToken token) =>
        ReadAsync(workspaceId, toolName, invocationId, token, Access.Read);

    public Task<InvocationProposalReadResult> ReadForReviewAsync(string workspaceId, string toolName,
        InvocationId invocationId, CapabilityToken token) =>
        ReadAsync(workspaceId, toolName, invocationId, token, Access.Review);

    public Task<InvocationProposalReadResult> ReadForResumeAsync(string workspaceId, string toolName,
        InvocationId invocationId, CapabilityToken token) =>
        ReadAsync(workspaceId, toolName, invocationId, token, Access.Resume);

    private async Task<InvocationProposalReadResult> ReadAsync(string workspaceId, string toolName,
        InvocationId invocationId, CapabilityToken token, Access access)
    {
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        token.CancellationToken.ThrowIfCancellationRequested();
        await AuthorizeEntryAsync();
        if (!Guid.TryParseExact(invocationId.ToString(), "N", out var guid) || guid == Guid.Empty)
            return new(null, "invalid-invocation-id");
        invocationId = InvocationId.From(guid.ToString("N"));
        var proposals = journal as IInvocationProposalJournal
            ?? throw new InvalidOperationException("UUID proposal access requires a proposal-capable journal.");

        var record = journal.Find(workspaceId, invocationId, token.CancellationToken);
        var approval = record is null || access == Access.Review
            ? proposals.FindApproval(workspaceId, invocationId, token.CancellationToken) : null;
        var subject = approval?.Subject ?? record?.Subject;
        var storedTool = approval?.ToolName ?? record?.ToolName;
        var operation = approval?.Operation ?? record?.Operation;
        var digest = approval?.InputDigest ?? record?.InputDigest;
        await AuthorizeEntryAsync();
        if (subject is null || storedTool != toolName || operation is null || digest is null
            || (access == Access.Review && approval is null)
            || (access != Access.Review && subject != token.IssuedTo))
            return new(null, "proposal-not-found");
        if (access == Access.Review && subject == token.IssuedTo)
            throw new UnauthorizedAccessException("An independent reviewer is required.");

        await AuthorizeOperationAsync();
        var request = proposals.FindProposal(workspaceId, invocationId, subject, toolName, operation, digest,
            token.CancellationToken);
        await AuthorizeEntryAsync();
        await AuthorizeOperationAsync();
        token.CancellationToken.ThrowIfCancellationRequested();
        return request is null ? new(null, "proposal-unavailable") : new(request, null);

        async Task AuthorizeEntryAsync()
        {
            await authorizer.AuthorizeAsync(token, "invocation:read", workspaceId);
            if (access == Access.Read)
                await authorizer.AuthorizeAsync(token, "invocation:proposal:read", workspaceId);
            if (access == Access.Review)
                await authorizer.AuthorizeAsync(token, "approval:decide", workspaceId);
            token.CancellationToken.ThrowIfCancellationRequested();
        }

        async Task AuthorizeOperationAsync()
        {
            if (access == Access.Review)
                await authorizer.AuthorizeAsync(token, ToolCapability.Approve(toolName, operation), workspaceId);
            if (access == Access.Resume)
                await authorizer.AuthorizeAsync(token, ToolCapability.Invoke(toolName, operation), workspaceId);
            token.CancellationToken.ThrowIfCancellationRequested();
        }
    }

    private enum Access { Read, Review, Resume }
}
