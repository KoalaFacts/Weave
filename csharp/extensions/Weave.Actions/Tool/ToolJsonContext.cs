using System.Text.Json.Serialization;

namespace Weave.Actions.Tool;

/// <summary>
/// Wire shape of a tool connection as returned by the silo's
/// <c>/api/workspaces/{id}/tools</c> endpoint. Internal to the action because
/// frontends consume the curated <see cref="ToolSummary"/> instead. Kept local
/// so the silo's response shape can change without rippling into a centralized
/// DTO surface.
/// </summary>
internal sealed record ToolWire
{
    public string ToolName { get; init; } = string.Empty;
    public string ToolType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Per-feature source-gen JSON context for the tool verbs. Each Phase 1 verb
/// folder owns its own context so the source-gen surface stays local and silo
/// response-shape changes don't ripple across the action layer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ToolWire))]
[JsonSerializable(typeof(ToolWire[]))]
[JsonSerializable(typeof(List<ToolWire>))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
