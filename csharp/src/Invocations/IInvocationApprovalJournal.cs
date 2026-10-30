using Weave.Shared.Ids;

namespace Weave.Invocations;

/// <summary>Approval consumption and attempt admission share the journal's transaction.</summary>
public interface IInvocationApprovalJournal : IInvocationJournal
{
    InvocationApproval? FindApproval(string workspaceId, InvocationId invocationId, CancellationToken cancellationToken);
    InvocationApprovalDecisionResult DecideApproval(string workspaceId, InvocationId invocationId,
        string planDigest, string decider, string tokenId, InvocationApprovalDecision decision,
        DateTimeOffset now, CancellationToken cancellationToken);
}
