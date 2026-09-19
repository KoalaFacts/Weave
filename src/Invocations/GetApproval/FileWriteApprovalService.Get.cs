using Weave.Security.Tokens;

namespace Weave.Invocations;

public sealed partial class FileWriteApprovalService
{
    public async Task<FileWriteApproval?> GetAsync(string workspace, string tool, InvocationId id, CapabilityToken token)
    {
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        await authorizer.AuthorizeAsync(token, "approval:read", workspace);
        token.CancellationToken.ThrowIfCancellationRequested();
        var record = store.FindApproval(workspace, Canonical(id), token.CancellationToken);
        if (record is null || record.ToolName != tool)
            return null;
        if (record.Subject != token.IssuedTo)
            await authorizer.AuthorizeAsync(token, "approval:decide", workspace);
        var plan = Unprotect(record);
        await authorizer.AuthorizeAsync(token, "approval:read", workspace);
        if (record.Subject != token.IssuedTo)
            await authorizer.AuthorizeAsync(token, "approval:decide", workspace);
        token.CancellationToken.ThrowIfCancellationRequested();
        return new FileWriteApproval(plan, record.PlanDigest, record.StateAt(timeProvider.GetUtcNow()),
            record.CreatedAt, record.ExpiresAt, record.DecidedBy, record.DecidedAt, record.CancelledBy, record.CancelledAt);
    }

    internal async Task<(ApprovalRecord Record, FileWriteApprovalPlan Plan)?> LoadForResumeAsync(
        string workspace, string tool, InvocationId id, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, ToolCapability.Invoke(tool, "write_file"), workspace);
        token.CancellationToken.ThrowIfCancellationRequested();
        var record = store.FindApproval(workspace, Canonical(id), token.CancellationToken);
        if (record is null || record.ToolName != tool || record.Subject != token.IssuedTo)
            return null;
        return (record, Unprotect(record));
    }

    private static InvocationId Canonical(InvocationId id)
    {
        if (!Guid.TryParseExact(id.ToString(), "N", out var guid) || guid == Guid.Empty)
            throw new ArgumentException("A nonzero 32-hex invocation ID is required.", nameof(id));
        return InvocationId.From(guid.ToString("N"));
    }
}
