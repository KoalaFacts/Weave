using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;
using Weave.Workspaces.Templates;

namespace Weave.Silo.VirtualActors;

public sealed class WorkspaceActorGrain : Grain, IWorkspaceActorGrain
{
    private readonly WorkspaceActor _actor;

    public WorkspaceActorGrain(
        IWorkspaceRuntime runtime,
        ILifecycleManager lifecycleManager,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<WorkspaceActor> logger,
        IWorkspaceRuntimeRecovery recovery,
        [PersistentState("workspace", "Default")] IPersistentState<WorkspaceState> state)
    {
        _actor = new WorkspaceActor(runtime, lifecycleManager, eventBus, timeProvider, logger,
            new OrleansActorState<WorkspaceState>(state), recovery);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<WorkspaceState> StartAsync(WorkspaceManifest manifest) => _actor.StartAsync(manifest);
    public Task StopAsync() => _actor.StopAsync();
    public Task<WorkspaceState> GetStateAsync() => _actor.GetStateAsync();
    public Task<WorkspaceRuntimeSnapshot> ObserveRuntimeAsync(CapabilityToken token, CancellationToken ct) => _actor.ObserveRuntimeAsync(token, ct);
    public Task<ContainerRecoveryResult> RecoverContainerAsync(ContainerId containerId, CapabilityToken token, string managementId, CancellationToken ct) =>
        _actor.RecoverContainerAsync(containerId, token, managementId, ct);
    public Task SetDaprToolInstallationEnabledAsync(string pluginName, bool enabled) =>
        _actor.SetDaprToolInstallationEnabledAsync(pluginName, enabled);
    public Task SetMcpToolInstallationEnabledAsync(string pluginName, bool enabled) =>
        _actor.SetMcpToolInstallationEnabledAsync(pluginName, enabled);
    public Task PinMcpToolContractAsync(string pluginName, string contractDigest) =>
        _actor.PinMcpToolContractAsync(pluginName, contractDigest);
}
