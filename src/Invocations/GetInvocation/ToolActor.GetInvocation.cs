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
        _identity.Ensure(token: token);
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        token.CancellationToken.ThrowIfCancellationRequested();
        await authorizer.AuthorizeAsync(token, "invocation:read", _identity.WorkspaceId);
        var record = journal.Find(_identity.WorkspaceId, invocationId, token.CancellationToken);
        return record is not null
            && string.Equals(record.Subject, token.IssuedTo, StringComparison.Ordinal)
            && string.Equals(record.ToolName, _identity.ToolName, StringComparison.Ordinal)
            ? record : null;
    }
}
