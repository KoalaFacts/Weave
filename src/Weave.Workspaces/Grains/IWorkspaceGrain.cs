using Orleans;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Grains;

public interface IWorkspaceGrain : IGrainWithStringKey
{
    Task<WorkspaceState> StartAsync(WorkspaceManifest manifest);
    Task StopAsync();
    Task<WorkspaceState> GetStateAsync();
}
