using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
namespace Weave.Workspaces.Runtime;

public interface IWorkspaceRuntime
{
    Guid InstanceId { get; }
    string RuntimeName { get; }
    Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceId workspaceId, WorkspaceManifest manifest, CancellationToken ct);
    Task TeardownAsync(WorkspaceId workspaceId, NetworkId? networkId,
        IReadOnlyList<ContainerId> containerIds, CancellationToken ct);
    Task<ContainerHandle> StartContainerAsync(ContainerSpec spec, CancellationToken ct);
    Task StopContainerAsync(ContainerId containerId, CancellationToken ct);
    Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct);
    Task DeleteNetworkAsync(NetworkId networkId, CancellationToken ct);
}
