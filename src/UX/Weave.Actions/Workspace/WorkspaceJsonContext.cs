using System.Text.Json.Serialization;

namespace Weave.Actions.Workspace;

/// <summary>
/// Wire shape of a workspace as returned by the silo's
/// <c>/api/workspaces/{id}</c> endpoint. Internal to the action because
/// frontends consume the curated <see cref="WorkspaceStatusSummary"/>
/// instead. Kept local so the silo's response shape can change without
/// rippling into a centralized DTO surface.
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
/// Per-feature source-gen JSON context for the workspace verbs. Each Phase 1
/// verb folder owns its own context so the source-gen surface stays local
/// and silo response-shape changes don't ripple across the action layer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WorkspaceWire))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
