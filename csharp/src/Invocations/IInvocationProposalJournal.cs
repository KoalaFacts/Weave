using Weave.Tools.Tool;

namespace Weave.Invocations;

/// <summary>Proposal contents and pending/admitted metadata share one durable transaction.</summary>
public interface IInvocationProposalJournal : IInvocationApprovalJournal
{
    InvocationClaim TryStart(InvocationRecord candidate, ToolInvocation proposal, string connectorKind,
        CancellationToken cancellationToken);

    ToolInvocation? FindProposal(string workspaceId, InvocationId invocationId, string subject,
        string toolName, string operation, string inputDigest, CancellationToken cancellationToken);
}
