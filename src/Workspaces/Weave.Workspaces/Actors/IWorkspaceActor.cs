using Weave.Workspaces.Models;

namespace Weave.Workspaces.Actors;

public interface IWorkspaceActor : IVirtualActorWithStringKey
{
    Task<WorkspaceState> StartAsync(WorkspaceManifest manifest);
    Task StopAsync();
    Task<WorkspaceState> GetStateAsync();
}
