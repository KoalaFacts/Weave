using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

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
        [PersistentState("workspace", "Default")] IPersistentState<WorkspaceState> state)
    {
        _actor = new WorkspaceActor(runtime, lifecycleManager, eventBus, timeProvider, logger,
            new OrleansActorState<WorkspaceState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<WorkspaceState> StartAsync(WorkspaceManifest manifest) => _actor.StartAsync(manifest);
    public Task StopAsync() => _actor.StopAsync();
    public Task<WorkspaceState> GetStateAsync() => _actor.GetStateAsync();
}
