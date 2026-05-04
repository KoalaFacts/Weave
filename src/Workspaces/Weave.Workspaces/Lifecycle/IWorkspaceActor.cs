using Weave.Workspaces.Models;

namespace Weave.Workspaces.Lifecycle;

public interface IWorkspaceActor
{
    Task<WorkspaceState> StartAsync(WorkspaceManifest manifest);
    Task StopAsync();
    Task<WorkspaceState> GetStateAsync();
}
