using Weave.Shared.Ids;

namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record AgentState
{
    [Id(0)] public required string AgentId { get; init; }
    [Id(1)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(2)] public required string AgentName { get; init; }
    [Id(3)] public AgentStatus Status { get; set; } = AgentStatus.Idle;
    [Id(4)] public string? Model { get; set; }
    [Id(5)] public List<string> ConnectedTools { get; init; } = [];
    [Id(6)] public List<AgentTaskInfo> ActiveTasks { get; init; } = [];
    [Id(7)] public int MaxConcurrentTasks { get; set; } = 1;
    [Id(8)] public DateTimeOffset? ActivatedAt { get; set; }
    [Id(9)] public DateTimeOffset? DeactivatedAt { get; set; }
    [Id(10)] public string? ErrorMessage { get; set; }
}

public enum AgentStatus
{
    Idle,
    Activating,
    Active,
    Busy,
    Deactivating,
    Error
}

[GenerateSerializer]
public sealed record AgentTaskInfo
{
    [Id(0)] public required AgentTaskId TaskId { get; init; }
    [Id(1)] public required string Description { get; init; }
    [Id(2)] public AgentTaskStatus Status { get; init; } = AgentTaskStatus.Pending;
    [Id(3)] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(4)] public DateTimeOffset? CompletedAt { get; init; }
}

public enum AgentTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}
