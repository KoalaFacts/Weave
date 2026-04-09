namespace Weave.Dashboard.Services;

public sealed class WeaveApiClient(HttpClient http)
{
    // === Workspaces ===

    public async Task<List<WorkspaceDto>> GetWorkspacesAsync(CancellationToken ct = default)
    {
        var result = await http.GetFromJsonAsync("/api/workspaces", DashboardJsonContext.Default.ListWorkspaceDto, ct);
        return result ?? [];
    }

    public async Task<WorkspaceDto?> GetWorkspaceAsync(string workspaceId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync($"/api/workspaces/{workspaceId}", DashboardJsonContext.Default.WorkspaceDto, ct);

    // === Agents ===

    public async Task<List<AgentDto>> GetAgentsAsync(string workspaceId, CancellationToken ct = default)
    {
        var result = await http.GetFromJsonAsync($"/api/workspaces/{workspaceId}/agents", DashboardJsonContext.Default.ListAgentDto, ct);
        return result ?? [];
    }

    public async Task<AgentChatResponseDto?> SendMessageAsync(
        string workspaceId,
        string agentName,
        string content,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/agents/{agentName}/messages",
            new SendMessageDto { Content = content, Role = "user" },
            DashboardJsonContext.Default.SendMessageDto,
            ct);
        using var _ = response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(DashboardJsonContext.Default.AgentChatResponseDto, ct);
    }

    // === Tools ===

    public async Task<List<ToolConnectionDto>> GetToolsAsync(string workspaceId, CancellationToken ct = default)
    {
        var result = await http.GetFromJsonAsync($"/api/workspaces/{workspaceId}/tools", DashboardJsonContext.Default.ListToolConnectionDto, ct);
        return result ?? [];
    }
}

// DTOs mirroring the Silo API contract responses

public sealed record SendMessageDto
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}

public sealed record WorkspaceDto
{
    public string WorkspaceId { get; init; } = "";
    public string? Name { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public int ContainerCount { get; init; }
    public string? ErrorMessage { get; init; }
}

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

public sealed record TaskDto
{
    public string TaskId { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record AgentChatResponseDto
{
    public string Content { get; init; } = "";
    public string ConversationId { get; init; } = "";
    public List<ConversationMessageDto> Messages { get; init; } = [];
    public bool UsedTools { get; init; }
    public string? Model { get; init; }
}

public sealed record ConversationMessageDto
{
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record ToolConnectionDto
{
    public string ToolName { get; init; } = "";
    public string ToolType { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
