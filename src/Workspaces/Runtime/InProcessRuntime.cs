using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
namespace Weave.Workspaces.Runtime;

/// <summary>
/// A lightweight runtime that runs entirely in-process with no containers or external services.
/// Tools configured as MCP servers are skipped — only in-process tool connectors are available.
/// </summary>
public sealed partial class InProcessRuntime(ILogger<InProcessRuntime> logger) : IWorkspaceRuntime
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string RuntimeName => "in-process";

    public Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceId workspaceId, WorkspaceManifest manifest, CancellationToken ct)
    {
        LogWorkspaceProvisioned(manifest.Name);

        return Task.FromResult(new WorkspaceEnvironment(
            workspaceId,
            NetworkId.From("local"),
            []));
    }

    public Task TeardownAsync(WorkspaceId workspaceId, NetworkId? networkId,
        IReadOnlyList<ContainerId> containerIds, CancellationToken ct)
    {
        LogWorkspaceTornDown(workspaceId);
        return Task.CompletedTask;
    }

    public Task<ContainerHandle> StartContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        LogContainerSkipped(spec.Name);
        return Task.FromResult(new ContainerHandle(
            ContainerId.From($"local-{spec.Name}"),
            spec.Name,
            spec.Image,
            spec.PortMappings));
    }

    public Task StopContainerAsync(ContainerId containerId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task<ContainerRuntimeCondition> ObserveContainerAsync(ContainerId containerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ContainerRuntimeCondition.Unsupported);
    }

    public Task<NetworkRuntimeCondition> ObserveNetworkAsync(NetworkId networkId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NetworkRuntimeCondition.Unsupported);
    }

    public Task<ContainerNetworkCondition> ObserveContainerNetworkAsync(ContainerId containerId, NetworkId networkId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ContainerNetworkCondition.Unsupported);
    }

    public Task<ContainerRecoveryResult> RecoverContainerAsync(ContainerId containerId, NetworkId requiredNetworkId, Func<Task> authorizeDispatch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ContainerRecoveryResult
        {
            ContainerId = containerId.ToString(),
            Condition = ContainerRuntimeCondition.Unsupported,
            Network = new NetworkRuntimeObservation { NetworkId = requiredNetworkId.ToString(), Condition = NetworkRuntimeCondition.Unsupported },
            NetworkAttachment = ContainerNetworkCondition.Unsupported
        });
    }

    public Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct)
    {
        return Task.FromResult(new NetworkHandle(NetworkId.From("local"), spec.Name));
    }

    public Task DeleteNetworkAsync(NetworkId networkId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceName} provisioned in-process (no containers)")]
    private partial void LogWorkspaceProvisioned(string workspaceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} torn down (in-process)")]
    private partial void LogWorkspaceTornDown(WorkspaceId workspaceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Container {ContainerName} skipped in in-process mode")]
    private partial void LogContainerSkipped(string containerName);
}
