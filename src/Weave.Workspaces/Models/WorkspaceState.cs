using Orleans;

namespace Weave.Workspaces.Models;

[GenerateSerializer]
public sealed record WorkspaceState
{
    [Id(0)] public required string WorkspaceId { get; init; }
    [Id(1)] public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Stopped;
    [Id(2)] public DateTimeOffset? StartedAt { get; set; }
    [Id(3)] public DateTimeOffset? StoppedAt { get; set; }
    [Id(4)] public List<string> ActiveAgents { get; init; } = [];
    [Id(5)] public List<string> ActiveTools { get; init; } = [];
    [Id(6)] public List<ContainerInfo> Containers { get; init; } = [];
    [Id(7)] public string? NetworkId { get; set; }
    [Id(8)] public string? ErrorMessage { get; set; }
}

public enum WorkspaceStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

[GenerateSerializer]
public sealed record ContainerInfo
{
    [Id(0)] public required string ContainerId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public required string Image { get; init; }
    [Id(3)] public ContainerStatus Status { get; init; }
}

public enum ContainerStatus
{
    Created,
    Running,
    Stopped,
    Error
}
