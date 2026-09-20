using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Tools.Tool;

namespace Weave.Silo.VirtualActors;

public interface IToolActorGrain : IToolActor, IGrainWithStringKey
{
    // RPC cancellation must be a method argument, not part of the serialized capability.
    Task<ToolResult> InvokeWithCancellationAsync(ToolInvocation invocation, CapabilityToken token,
        CancellationToken cancellationToken);
    Task<InvocationRecord?> GetInvocationWithCancellationAsync(InvocationId invocationId, CapabilityToken token,
        CancellationToken cancellationToken);
    Task<InvocationApproval?> GetApprovalWithCancellationAsync(InvocationId invocationId, CapabilityToken token,
        CancellationToken cancellationToken);
    Task<InvocationApprovalReviewResult> ReviewApprovalWithCancellationAsync(ToolInvocation invocation, CapabilityToken token,
        CancellationToken cancellationToken);
    Task<InvocationApprovalDecisionResult> DecideReviewedApprovalWithCancellationAsync(ToolInvocation invocation,
        string planDigest, InvocationApprovalDecision decision, CapabilityToken token, CancellationToken cancellationToken);
}
