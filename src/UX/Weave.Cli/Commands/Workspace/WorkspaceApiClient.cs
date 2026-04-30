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

        private static string FormatHttpError(int statusCode, string? reason, string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return $"HTTP {statusCode} {reason}";

            try
            {
                using var doc = JsonDocument.Parse(body);
                // RFC 7807 ProblemDetails: extract 'detail' field for cleaner messages
                if (doc.RootElement.TryGetProperty("detail", out var detail))
                    return detail.GetString() ?? body.Trim();
                // Fallback to 'title' if 'detail' not present
                if (doc.RootElement.TryGetProperty("title", out var title))
                    return title.GetString() ?? body.Trim();
            }
            catch
            {
                // If JSON parsing fails, fall back to raw body
            }

            return body.Trim();
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
