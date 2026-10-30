using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Tools.Tool;

public sealed partial class ToolActor
{
    /// <summary>
    /// Requires invocation:read in this workspace and the original validated subject.
    /// The actor need not be connected, so persisted outcomes remain queryable after restart.
    /// This is not an administrator export or an unauthenticated HTTP route.
    /// </summary>
    public async Task<InvocationRecord?> GetInvocationAsync(InvocationId invocationId, CapabilityToken token)
    {
        // A query has no ToolSpec/ToolInvocation to resolve. Use only the identity
        // already established by runtime activation or a previous connection.
        if (string.IsNullOrWhiteSpace(_identity.WorkspaceId) || string.IsNullOrWhiteSpace(_identity.ToolName))
            throw new InvalidOperationException("Tool identity must be established before querying invocations.");
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        token.CancellationToken.ThrowIfCancellationRequested();
        await authorizer.AuthorizeAsync(token, "invocation:read", _identity.WorkspaceId);
        if (!Guid.TryParseExact(invocationId.ToString(), "N", out var guid) || guid == Guid.Empty)
            return null;
        var record = journal.Find(_identity.WorkspaceId, InvocationId.From(guid.ToString("N")), token.CancellationToken);
        await authorizer.AuthorizeAsync(token, "invocation:read", _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        return record is not null
            && string.Equals(record.Subject, token.IssuedTo, StringComparison.Ordinal)
            && string.Equals(record.ToolName, _identity.ToolName, StringComparison.Ordinal)
            ? record : null;
    }
}
