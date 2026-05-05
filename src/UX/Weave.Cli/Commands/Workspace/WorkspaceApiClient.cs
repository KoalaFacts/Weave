using System.Net.Http.Json;
using System.Text.Json;
using Weave.Actions;
using Weave.Workspaces.Manifest;
namespace Weave.Cli.Commands;

internal sealed class WorkspaceApiClient : IDisposable, ISiloProbe
{
    private readonly HttpClient _httpClient;

    public WorkspaceApiClient(string? baseUrl = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl ?? CliApiHttp.ResolveBaseUrl(), UriKind.Absolute)
        };
    }

    public async Task<ApiWorkspaceResponse> StartWorkspaceAsync(WorkspaceManifest manifest, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/workspaces", new ApiStartWorkspaceRequest
        {
            Manifest = manifest
        }, CliApiJsonContext.Default.ApiStartWorkspaceRequest, cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiWorkspaceResponse, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty start response.");
    }

    public async Task StopWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/workspaces/{workspaceId}", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    public async Task<ApiWorkspaceResponse> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiWorkspaceResponse, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty payload for '/api/workspaces/'.");
    }

    public async Task<IReadOnlyList<ApiAgentResponse>> GetAgentsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/agents", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListApiAgentResponse, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty payload for '/api/workspaces/{workspaceId}/agents'.");
    }

    public async Task<IReadOnlyList<ApiToolResponse>> GetToolsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/tools", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
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
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
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
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ApiChatResponse, cancellationToken))
            ?? throw new InvalidOperationException("Agent API returned an empty chat response.");
    }

    public void Dispose() => _httpClient.Dispose();

    // --- Skills ---

    public async Task<IReadOnlyList<JsonElement>> GetSkillsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/skills", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListJsonElement, cancellationToken))
            ?? [];
    }

    public async Task PostSkillAsync(string workspaceId, JsonElement skill, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/skills", skill, CliApiJsonContext.Default.JsonElement, cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    // --- Channels ---

    public async Task<IReadOnlyList<JsonElement>> GetChannelsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/api/workspaces/{workspaceId}/channels", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListJsonElement, cancellationToken))
            ?? [];
    }

    public async Task PostChannelAsync(string workspaceId, JsonElement channel, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/channels", channel, CliApiJsonContext.Default.JsonElement, cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
    }

    // --- Templates ---

    public async Task<IReadOnlyList<JsonElement>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("/api/templates", cancellationToken);
        await CliApiHttp.EnsureSuccessOrThrowAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(CliApiJsonContext.Default.ListJsonElement, cancellationToken))
            ?? [];
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
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

    private static string? ResolvePath(string manifestDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(Path.Combine(manifestDirectory, path));
    }
}
