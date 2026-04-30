using System.Net.Http.Json;
using System.Text.Json;
using Weave.Shared;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public WorkspaceApiClient(string? baseUrl = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl ?? ResolveBaseUrl(), UriKind.Absolute)
        };
    }

    public async Task<ApiWorkspaceResponse> StartWorkspaceAsync(WorkspaceManifest manifest, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/workspaces", new ApiStartWorkspaceRequest
        {
            Manifest = manifest
        }, CliApiJsonContext.Default.ApiStartWorkspaceRequest, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiWorkspaceResponse, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty start response.");
    }

    public async Task StopWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/workspaces/{workspaceId}", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    public async Task<ApiWorkspaceResponse> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiWorkspaceResponse, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty payload for '/api/workspaces/'.");
    }

    public async Task<IReadOnlyList<ApiAgentResponse>> GetAgentsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/agents", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiAgentResponse, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty payload for '/api/workspaces/{workspaceId}/agents'.");
    }

    public async Task<IReadOnlyList<ApiToolResponse>> GetToolsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/tools", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiToolResponse, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty payload for '/api/workspaces/{workspaceId}/tools'.");
    }

    public async Task<IReadOnlyList<ApiTaskResponse>> GetTasksAsync(
        string workspaceId,
        string agentName,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"/api/workspaces/{workspaceId}/agents/{Uri.EscapeDataString(agentName)}/tasks",
            cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiTaskResponse, cancellationToken))
            ?? throw new InvalidOperationException("Agent API returned an empty payload for tasks.");
    }

    // --- Chat ---

    public async Task<ApiChatResponse> SendAgentMessageAsync(
        string workspaceId,
        string agentName,
        string content,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/agents/{agentName}/messages",
            new ApiSendMessageRequest { Content = content },
            CliApiJsonContext.Default.ApiSendMessageRequest,
            cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiChatResponse, cancellationToken))
            ?? throw new InvalidOperationException("Agent API returned an empty chat response.");
    }

    // ── Error handling ────────────────────────────────────────────
    //
    // HttpResponseMessage.EnsureSuccessStatusCode() throws a message
    // like "Response status code does not indicate success: 409
    // (Conflict)" — which is what you'd see without any clue WHY.
    // The Silo emits RFC 7807 ProblemDetails on errors, so the
    // response body has the actual reason. This helper reads it and
    // routes through FormatHttpError so the user's message lands on
    // screen instead of a generic status code.

    private static async Task EnsureSuccessOrThrowAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try
        { body = await response.Content.ReadAsStringAsync(ct); }
        catch { body = string.Empty; }

        var message = FormatHttpError((int)response.StatusCode, response.ReasonPhrase, body);
        throw new HttpRequestException(message, inner: null, response.StatusCode);
    }

    // TODO(you) — shape this however you want the CLI to present
    // Silo errors. See the request above the method for options.
    private static string FormatHttpError(int statusCode, string? reason, string body)
    {
        // BASELINE: echo the raw body verbatim. Works but verbose.
        // Consider parsing ProblemDetails JSON for a cleaner one-liner.
        var safeBody = string.IsNullOrWhiteSpace(body) ? "(no response body)" : body.Trim();
        return $"HTTP {statusCode} {reason}: {safeBody}";
    }

    public void Dispose() => _httpClient.Dispose();

    // --- Skills ---

    public async Task<IReadOnlyList<JsonElement>> GetSkillsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/skills", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListJsonElement, cancellationToken))
            ?? [];
    }

    public async Task PostSkillAsync(string workspaceId, JsonElement skill, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/skills", skill, CliApiJsonContext.Default.JsonElement, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    // --- Channels ---

    public async Task<IReadOnlyList<JsonElement>> GetChannelsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/channels", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListJsonElement, cancellationToken))
            ?? [];
    }

    public async Task PostChannelAsync(string workspaceId, JsonElement channel, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/channels", channel, CliApiJsonContext.Default.JsonElement, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    // --- Templates ---

    public async Task<IReadOnlyList<JsonElement>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("/api/templates", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListJsonElement, cancellationToken))
            ?? [];
    }

    // --- Marketplace ---

    public async Task<IReadOnlyList<ApiMarketplaceItemResponse>> GetMarketplaceItemsAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("/api/marketplace", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiMarketplaceItemResponse, cancellationToken))
            ?? [];
    }

    public async Task<IReadOnlyList<ApiMarketplaceItemResponse>> SearchMarketplaceAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/marketplace/search?q={Uri.EscapeDataString(query)}", cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiMarketplaceItemResponse, cancellationToken))
            ?? [];
    }

    public async Task<ApiMarketplaceItemResponse?> GetMarketplaceItemAsync(string itemId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/marketplace/{Uri.EscapeDataString(itemId)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiMarketplaceItemResponse, cancellationToken);
    }

    public async Task<ApiMarketplaceItemResponse> SubmitMarketplaceItemAsync(
        string name, string description, string category, string version, string author, string[] tags,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/marketplace", new ApiSubmitMarketplaceRequest
        {
            Name = name,
            Description = description,
            Category = category,
            Version = version,
            Author = author,
            Tags = [.. tags]
        }, CliApiJsonContext.Default.ApiSubmitMarketplaceRequest, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiMarketplaceItemResponse, cancellationToken))
            ?? throw new InvalidOperationException("Marketplace API returned an empty response.");
    }

    public async Task<ApiMarketplaceItemResponse> PublishMarketplaceItemAsync(
        string itemId, string reviewerId, bool approved, string? notes, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/marketplace/{Uri.EscapeDataString(itemId)}/publish", new ApiPublishMarketplaceRequest
        {
            ReviewerId = reviewerId,
            Approved = approved,
            Notes = notes
        }, CliApiJsonContext.Default.ApiPublishMarketplaceRequest, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiMarketplaceItemResponse, cancellationToken))
            ?? throw new InvalidOperationException("Marketplace API returned an empty response.");
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var response = await _httpClient.GetAsync("/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static WorkspaceManifest PrepareManifest(WorkspaceManifest manifest, string manifestDirectory)
    {
        return manifest with
        {
            Agents = manifest.Agents.ToDictionary(
                static kvp => kvp.Key,
                kvp => kvp.Value with
                {
                    SystemPromptFile = ResolvePath(manifestDirectory, kvp.Value.SystemPromptFile)
                },
                StringComparer.Ordinal)
        };
    }

    public static string GetWorkspaceStatePath(string manifestPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidOperationException("Unable to determine the workspace directory.");
        return Path.Combine(directory, ".weave", "workspace-id");
    }

    private static string ResolveBaseUrl() =>
        Environment.GetEnvironmentVariable("WEAVE_API_URL") ?? $"http://localhost:{WeavePorts.SiloHttp}";

    private static string? ResolvePath(string manifestDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(Path.Combine(manifestDirectory, path));
    }
}

internal sealed record ApiStartWorkspaceRequest
{
    public required WorkspaceManifest Manifest { get; init; }
}

internal sealed record ApiWorkspaceResponse
{
    public required string WorkspaceId { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public int ContainerCount { get; init; }
    public string? ErrorMessage { get; init; }
}

internal sealed record ApiAgentResponse
{
    public required string AgentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentName { get; init; }
    public required string Status { get; init; }
    public string? Model { get; init; }
    public List<string> ConnectedTools { get; init; } = [];
    public List<ApiTaskResponse> ActiveTasks { get; init; } = [];
    public DateTimeOffset? ActivatedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

internal sealed record ApiTaskResponse
{
    public required string TaskId { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

internal sealed record ApiToolResponse
{
    public required string ToolName { get; init; }
    public required string ToolType { get; init; }
    public required string Status { get; init; }
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

internal sealed record ApiMarketplaceItemResponse
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required string Status { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset? PublishedAt { get; init; }
    public int InstallCount { get; init; }
    public double Rating { get; init; }
    public int RatingCount { get; init; }
}

internal sealed record ApiSubmitMarketplaceRequest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public List<string> Tags { get; init; } = [];
}

internal sealed record ApiPublishMarketplaceRequest
{
    public required string ReviewerId { get; init; }
    public required bool Approved { get; init; }
    public string? Notes { get; init; }
}

internal sealed record ApiSendMessageRequest
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}

internal sealed record ApiChatResponse
{
    public required string Content { get; init; }
    public required string ConversationId { get; init; }
    public required bool UsedTools { get; init; }
    public List<ApiConversationMessage> Messages { get; init; } = [];
    public string? Model { get; init; }
}

internal sealed record ApiConversationMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
