using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Runtime;

/// <summary>
/// A lightweight runtime that runs entirely in-process with no containers or external services.
/// Tools configured as MCP servers are skipped — only in-process tool connectors are available.
/// </summary>
public sealed partial class InProcessRuntime(ILogger<InProcessRuntime> logger) : IWorkspaceRuntime
{
    public string RuntimeName => "in-process";

    public Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceManifest manifest, CancellationToken ct)
    {
        LogWorkspaceProvisioned(manifest.Name);

        return Task.FromResult(new WorkspaceEnvironment(
            WorkspaceId.From(manifest.Name),
            NetworkId.From("local"),
            []));
    }

    public Task TeardownAsync(WorkspaceId workspaceId, CancellationToken ct)
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
