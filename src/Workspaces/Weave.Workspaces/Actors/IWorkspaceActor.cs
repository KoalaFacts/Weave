using Weave.Workspaces.Models;

namespace Weave.Workspaces.Actors;

public interface IWorkspaceActor
{
    Task<WorkspaceState> StartAsync(WorkspaceManifest manifest);
    Task StopAsync();
    Task<WorkspaceState> GetStateAsync();
}
