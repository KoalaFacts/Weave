using System.Net.Http.Json;
using System.Text.Json;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

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
        }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiWorkspaceResponse>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException("Workspace API returned an empty start response.");
    }

    public async Task StopWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.DeleteAsync($"/api/workspaces/{workspaceId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ApiWorkspaceResponse> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {
        return await GetAsync<ApiWorkspaceResponse>($"/api/workspaces/{workspaceId}", cancellationToken);
    }

    public async Task<IReadOnlyList<ApiAgentResponse>> GetAgentsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        return await GetAsync<List<ApiAgentResponse>>($"/api/workspaces/{workspaceId}/agents", cancellationToken);
    }

    public async Task<IReadOnlyList<ApiToolResponse>> GetToolsAsync(string workspaceId, CancellationToken cancellationToken)
    {
        return await GetAsync<List<ApiToolResponse>>($"/api/workspaces/{workspaceId}/tools", cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

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

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken))
            ?? throw new InvalidOperationException($"Workspace API returned an empty payload for '{path}'.");
    }

    private static string ResolveBaseUrl() =>
        Environment.GetEnvironmentVariable("WEAVE_API_URL") ?? "http://localhost:9401";

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
