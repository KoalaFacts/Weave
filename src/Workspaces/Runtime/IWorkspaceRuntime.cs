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
    Task<ContainerRuntimeCondition> ObserveContainerAsync(ContainerId containerId, CancellationToken ct);
    Task<NetworkRuntimeCondition> ObserveNetworkAsync(NetworkId networkId, CancellationToken ct);
    Task<ContainerNetworkCondition> ObserveContainerNetworkAsync(ContainerId containerId, NetworkId networkId, CancellationToken ct);
    Task<ContainerRecoveryResult> RecoverContainerAsync(ContainerId containerId, NetworkId requiredNetworkId, Func<Task> authorizeDispatch, CancellationToken ct);
    Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct);
    Task DeleteNetworkAsync(NetworkId networkId, CancellationToken ct);
}
