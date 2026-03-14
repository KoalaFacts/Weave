using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record AgentState
{
    [Id(0)] public string AgentId { get; set; } = string.Empty;
    [Id(1)] public WorkspaceId WorkspaceId { get; set; } = WorkspaceId.Empty;
    [Id(2)] public string AgentName { get; set; } = string.Empty;
    [Id(3)] public AgentStatus Status { get; set; } = AgentStatus.Idle;
    [Id(4)] public string? Model { get; set; }
    [Id(5)] public List<string> ConnectedTools { get; init; } = [];
    [Id(6)] public List<AgentTaskInfo> ActiveTasks { get; init; } = [];
    [Id(7)] public int MaxConcurrentTasks { get; set; } = 1;
    [Id(8)] public DateTimeOffset? ActivatedAt { get; set; }
    [Id(9)] public DateTimeOffset? DeactivatedAt { get; set; }
    [Id(10)] public string? ErrorMessage { get; set; }
    [Id(11)] public List<ConversationMessage> History { get; init; } = [];
    [Id(12)] public DateTimeOffset? LastActive { get; set; }
    [Id(13)] public int TotalTasksCompleted { get; set; }
    [Id(14)] public AgentDefinition? Definition { get; set; }
    [Id(15)] public string? ConversationId { get; set; }
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
    [Id(2)] public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    [Id(3)] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(4)] public DateTimeOffset? CompletedAt { get; set; }
    [Id(5)] public string? ResultSummary { get; set; }
}

public enum AgentTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

[GenerateSerializer]
public sealed record ConversationMessage
{
    [Id(0)] public required string Role { get; init; }
    [Id(1)] public required string Content { get; init; }
    [Id(2)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
