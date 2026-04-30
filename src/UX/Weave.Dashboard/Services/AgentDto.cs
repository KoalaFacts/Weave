namespace Weave.Dashboard.Services;

public sealed record AgentDto
{
    public string AgentId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Model { get; init; }
    public List<string> ConnectedTools { get; init; } = [];
    public List<TaskDto> ActiveTasks { get; init; } = [];
    public DateTimeOffset? ActivatedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
