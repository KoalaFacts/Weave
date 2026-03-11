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

public sealed record WorkspaceEnvironment(
    WorkspaceId WorkspaceId,
    NetworkId NetworkId,
    IReadOnlyList<ContainerHandle> Containers);

public sealed record ContainerHandle(
    ContainerId ContainerId,
    string Name,
    string Image,
    IReadOnlyDictionary<int, int> PortMappings);

public sealed record ContainerSpec
{
    public required string Name { get; init; }
    public required string Image { get; init; }
    public Dictionary<string, string> Environment { get; init; } = [];
    public Dictionary<int, int> PortMappings { get; init; } = [];
    public List<MountConfig> Mounts { get; init; } = [];
    public NetworkId? NetworkId { get; init; }
    public List<string> Command { get; init; } = [];
    public bool ReadOnly { get; init; }
    public bool DropAllCapabilities { get; init; } = true;
    public bool NoNetwork { get; init; }
}

public sealed record NetworkSpec
{
    public required string Name { get; init; }
    public string? Subnet { get; init; }
}

public sealed record NetworkHandle(NetworkId NetworkId, string Name);
