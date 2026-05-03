using System.Text.Json.Serialization;
using Weave.Agents.Models;

namespace Weave.Silo.Api;

public sealed record AgentResponse
{
    public required string AgentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentName { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<AgentStatus>))]
    public required AgentStatus Status { get; init; }
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
        Status = state.Status,
        Model = state.Model,
        ConnectedTools = [.. state.ConnectedTools],
        ActiveTasks = state.ActiveTasks.Select(TaskResponse.FromInfo).ToList(),
        ActivatedAt = state.ActivatedAt,
        ErrorMessage = state.ErrorMessage
    };
}
