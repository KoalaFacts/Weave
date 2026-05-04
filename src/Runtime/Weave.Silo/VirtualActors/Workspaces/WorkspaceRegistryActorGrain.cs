using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;
using Weave.Workspaces.Models;

namespace Weave.Silo.VirtualActors;

public sealed class WorkspaceRegistryActorGrain : Grain, IWorkspaceRegistryActorGrain
{
    private readonly WorkspaceRegistryActor _actor;

    public WorkspaceRegistryActorGrain(
        [PersistentState("workspace-registry", "Default")] IPersistentState<WorkspaceRegistryState> state)
    {
        _actor = new WorkspaceRegistryActor(new OrleansActorState<WorkspaceRegistryState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RegisterAsync(string workspaceId) => _actor.RegisterAsync(workspaceId);
    public Task UnregisterAsync(string workspaceId) => _actor.UnregisterAsync(workspaceId);
    public Task<IReadOnlyList<string>> GetWorkspaceIdsAsync() => _actor.GetWorkspaceIdsAsync();
}
