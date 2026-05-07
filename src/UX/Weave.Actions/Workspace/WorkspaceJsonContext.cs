using System.Text.Json.Serialization;
using Weave.Workspaces.Manifest;

namespace Weave.Actions.Workspace;

/// <summary>
/// Wire shape of a workspace as returned by the silo's
/// <c>/api/workspaces/{id}</c> and <c>POST /api/workspaces</c> endpoints.
/// Internal to the action because frontends consume the curated
/// <see cref="WorkspaceStatusSummary"/> instead. Kept local so the silo's
/// response shape can change without rippling into a centralized DTO surface.
/// </summary>
internal sealed record WorkspaceWire
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string Status { get; init; } = string.Empty;
    public int ContainerCount { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request envelope for <c>POST /api/workspaces</c>. The silo's binder maps
/// this to its own <c>StartWorkspaceRequest</c> shape; we declare a local
/// wire so the action stays decoupled from the silo's contract types.
/// </summary>
internal sealed record StartWorkspaceWire
{
    public required WorkspaceManifest Manifest { get; init; }
}

/// <summary>
/// Minimal subset of RFC 7807 ProblemDetails the start/stop verbs need to
/// surface a useful message. <c>Errors</c> populates on 400 (validation
/// problem); <c>Detail</c> populates on 409 (conflict).
/// </summary>
internal sealed record WorkspaceProblemWire
{
    public Dictionary<string, string[]>? Errors { get; init; }
    public string? Detail { get; init; }
    public string? Title { get; init; }
}

/// <summary>
/// Per-feature source-gen JSON context for the workspace verbs. Each Phase 1
/// verb folder owns its own context so the source-gen surface stays local
/// and silo response-shape changes don't ripple across the action layer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkspaceWire))]
[JsonSerializable(typeof(StartWorkspaceWire))]
[JsonSerializable(typeof(WorkspaceManifest))]
[JsonSerializable(typeof(WorkspaceProblemWire))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
