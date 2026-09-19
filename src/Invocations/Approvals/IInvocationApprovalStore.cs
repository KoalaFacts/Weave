namespace Weave.Invocations;

public interface IInvocationApprovalStore
{
    ApprovalRecord? ProposeApproval(ApprovalRecord candidate, CancellationToken cancellationToken);
    ApprovalRecord? FindApproval(string workspaceId, InvocationId id, CancellationToken cancellationToken);
    bool TryDecideApproval(string workspaceId, InvocationId id, string digest, ApprovalDecision decision,
        string subject, string tokenId, DateTimeOffset tokenExpiry, DateTimeOffset now, CancellationToken cancellationToken);
}
