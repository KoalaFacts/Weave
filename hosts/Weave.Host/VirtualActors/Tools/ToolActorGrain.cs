using Weave.Invocations;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Discovery;
using Weave.Tools.Marketplace;
using Weave.Tools.Tool;

namespace Weave.Silo.VirtualActors;

public sealed class ToolActorGrain : Grain, IToolActorGrain
{
    private readonly ToolActor _actor;

    public ToolActorGrain(
        IVirtualActorProvider actors,
        IToolDiscoveryService discovery,
        ILeakScanner leakScanner,
        ICapabilityAuthorizer authorizer,
        ILifecycleManager lifecycleManager,
        IEventBus eventBus,
        ILogger<ToolActor> logger,
        IInvocationJournal journal,
        TimeProvider timeProvider)
    {
        _actor = new ToolActor(actors, discovery, leakScanner, authorizer, lifecycleManager, eventBus, logger,
            journal, timeProvider);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token) =>
        _actor.ConnectAsync(definition, token);

    public Task DisconnectAsync() => _actor.DisconnectAsync();

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token) =>
        _actor.InvokeAsync(invocation, token);

    public Task<InvocationRecord?> GetInvocationAsync(InvocationId invocationId, CapabilityToken token) =>
        _actor.GetInvocationAsync(invocationId, token);

    public Task<InvocationApproval?> GetApprovalAsync(InvocationId invocationId, CapabilityToken token) =>
        _actor.GetApprovalAsync(invocationId, token);

    public Task<InvocationApprovalReviewResult> ReviewApprovalAsync(ToolInvocation invocation, CapabilityToken token) =>
        _actor.ReviewApprovalAsync(invocation, token);

    public Task<InvocationApprovalDecisionResult> DecideApprovalAsync(InvocationId invocationId,
        string planDigest, InvocationApprovalDecision decision, CapabilityToken token) =>
        _actor.DecideApprovalAsync(invocationId, planDigest, decision, token);

    public Task<ToolResult> InvokeWithCancellationAsync(ToolInvocation invocation, CapabilityToken token,
        CancellationToken cancellationToken) =>
        _actor.InvokeAsync(invocation, token with { CancellationToken = cancellationToken });

    public Task<InvocationRecord?> GetInvocationWithCancellationAsync(InvocationId invocationId, CapabilityToken token,
        CancellationToken cancellationToken) =>
        _actor.GetInvocationAsync(invocationId, token with { CancellationToken = cancellationToken });

    public Task<InvocationApproval?> GetApprovalWithCancellationAsync(InvocationId invocationId, CapabilityToken token,
        CancellationToken cancellationToken) =>
        _actor.GetApprovalAsync(invocationId, token with { CancellationToken = cancellationToken });

    public Task<InvocationApprovalReviewResult> ReviewApprovalWithCancellationAsync(ToolInvocation invocation, CapabilityToken token,
        CancellationToken cancellationToken) =>
        _actor.ReviewApprovalAsync(invocation, token with { CancellationToken = cancellationToken });

    public Task<ToolSchema> GetSchemaAsync() => _actor.GetSchemaAsync();
    public Task<ToolHandle?> GetHandleAsync() => _actor.GetHandleAsync();
}
