using Weave.Shared.Ids;

namespace Weave.Workspaces.Models;
public sealed record WorkspaceState
{
    public WorkspaceId WorkspaceId { get; set; } = WorkspaceId.Empty;
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Stopped;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public List<string> ActiveAgents { get; init; } = [];
    public List<string> ActiveTools { get; init; } = [];
    public List<ContainerInfo> Containers { get; init; } = [];
    public NetworkId? NetworkId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Name { get; set; }
}

public enum WorkspaceStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}
public sealed record ContainerInfo
{
    public required ContainerId ContainerId { get; init; }
    public required string Name { get; init; }
    public required string Image { get; init; }
    public ContainerStatus Status { get; init; }
}

public enum ContainerStatus
{
    Created,
    Running,
    Stopped,
    Error
}
