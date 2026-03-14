using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Silo.Api;

// === Workspace Contracts ===

public sealed record StartWorkspaceRequest
{
    public required WorkspaceManifest Manifest { get; init; }
}

public sealed record WorkspaceResponse
{
    public required string WorkspaceId { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public int ContainerCount { get; init; }
    public string? ErrorMessage { get; init; }

    public static WorkspaceResponse FromState(WorkspaceState state) => new()
    {
        WorkspaceId = state.WorkspaceId.ToString(),
        Status = state.Status.ToString(),
        StartedAt = state.StartedAt,
        StoppedAt = state.StoppedAt,
        NetworkId = state.NetworkId?.ToString(),
        ContainerCount = state.Containers.Count,
        ErrorMessage = state.ErrorMessage
    };
}

// === Agent Contracts ===

public sealed record ActivateAgentRequest
{
    public required AgentDefinition Definition { get; init; }
}

public sealed record SubmitTaskRequest
{
    public required string Description { get; init; }
}

public sealed record SendMessageRequest
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}

public sealed record CompleteTaskRequest
{
    public required bool Success { get; init; }
}

public sealed record AgentResponse
{
    public required string AgentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentName { get; init; }
    public required string Status { get; init; }
    public string? Model { get; init; }
    public List<string> ConnectedTools { get; init; } = [];
    public List<TaskResponse> ActiveTasks { get; init; } = [];
    public DateTimeOffset? ActivatedAt { get; init; }
    public string? ErrorMessage { get; init; }

    public static AgentResponse FromState(AgentState state) => new()
    {
        AgentId = state.AgentId,
        WorkspaceId = state.WorkspaceId.ToString(),
        AgentName = state.AgentName,
        Status = state.Status.ToString(),
        Model = state.Model,
        ConnectedTools = state.ConnectedTools,
        ActiveTasks = state.ActiveTasks.Select(TaskResponse.FromInfo).ToList(),
        ActivatedAt = state.ActivatedAt,
        ErrorMessage = state.ErrorMessage
    };
}

public sealed record TaskResponse
{
    public required string TaskId { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public static TaskResponse FromInfo(AgentTaskInfo info) => new()
    {
        TaskId = info.TaskId.ToString(),
        Description = info.Description,
        Status = info.Status.ToString(),
        CreatedAt = info.CreatedAt,
        CompletedAt = info.CompletedAt
    };
}

public sealed record AgentChatResponse
{
    public required string Content { get; init; }
    public required string ConversationId { get; init; }
    public List<ConversationMessageResponse> Messages { get; init; } = [];
    public bool UsedTools { get; init; }
    public string? Model { get; init; }

    public static AgentChatResponse FromResponse(Weave.Agents.Models.AgentChatResponse response) => new()
    {
        Content = response.Content,
        ConversationId = response.ConversationId,
        UsedTools = response.UsedTools,
        Model = response.Model,
        Messages = response.Messages.Select(ConversationMessageResponse.FromMessage).ToList()
    };
}

public sealed record ConversationMessageResponse
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    public static ConversationMessageResponse FromMessage(ConversationMessage message) => new()
    {
        Role = message.Role,
        Content = message.Content,
        Timestamp = message.Timestamp
    };
}

// === Tool Contracts ===

public sealed record ToolConnectionResponse
{
    public required string ToolName { get; init; }
    public required string ToolType { get; init; }
    public required string Status { get; init; }
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }

    public static ToolConnectionResponse FromConnection(ToolConnection conn) => new()
    {
        ToolName = conn.ToolName,
        ToolType = conn.ToolType,
        Status = conn.Status.ToString(),
        Endpoint = conn.Endpoint,
        ConnectedAt = conn.ConnectedAt,
        ErrorMessage = conn.ErrorMessage
    };
}
