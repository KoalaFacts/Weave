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
        await EnsureSuccessOrThrowAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(DashboardJsonContext.Default.AgentChatResponseDto, ct);
    }

    // Mirror the CLI's helper — read the ProblemDetails body before
    // throwing so the Blazor error boundary sees the Silo's actual
    // reason, not just "400 Bad Request". See
    // docs/best-practices.md — "Never call EnsureSuccessStatusCode()".
    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try
        { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { body = string.Empty; }

        var detail = string.IsNullOrWhiteSpace(body) ? "(no response body)" : body.Trim();
        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {detail}",
            inner: null,
            response.StatusCode);
    }

    // === Tools ===

    public async Task<List<ToolConnectionDto>> GetToolsAsync(string workspaceId, CancellationToken ct = default)
    {
        var result = await http.GetFromJsonAsync($"/api/workspaces/{workspaceId}/tools", DashboardJsonContext.Default.ListToolConnectionDto, ct);
        return result ?? [];
    }

    // === Capability audit ===

    public async Task<List<CapabilityAuditEntryDto>> GetRecentCapabilityAuditAsync(int limit = 200, CancellationToken ct = default)
    {
        var result = await http.GetFromJsonAsync($"/api/audit/capability?limit={limit}", DashboardJsonContext.Default.ListCapabilityAuditEntryDto, ct);
        return result ?? [];
    }

    public async Task<List<CapabilityAuditEntryDto>> GetCapabilityAuditByTokenAsync(string tokenId, CancellationToken ct = default)
    {
        var result = await http.GetFromJsonAsync($"/api/audit/capability/{Uri.EscapeDataString(tokenId)}", DashboardJsonContext.Default.ListCapabilityAuditEntryDto, ct);
        return result ?? [];
    }
}
