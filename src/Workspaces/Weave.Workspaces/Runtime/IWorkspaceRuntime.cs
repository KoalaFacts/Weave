using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Runtime;

public interface IWorkspaceRuntime
{
    string RuntimeName { get; }
    Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceManifest manifest, CancellationToken ct);
    Task TeardownAsync(WorkspaceId workspaceId, CancellationToken ct);
    Task<ContainerHandle> StartContainerAsync(ContainerSpec spec, CancellationToken ct);
    Task StopContainerAsync(ContainerId containerId, CancellationToken ct);
    Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct);
    Task DeleteNetworkAsync(NetworkId networkId, CancellationToken ct);
}
