using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Actions.Agent;

/// <summary>
/// Wire shape of an agent as returned by the silo's
/// <c>/api/workspaces/{id}/agents</c> endpoint. Internal to the action
/// because frontends consume the curated <see cref="AgentSummary"/> instead.
/// Kept local so the silo's response shape can change without rippling into
/// a centralized DTO surface.
/// </summary>
internal sealed record AgentWire
{
    public string AgentName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Model { get; init; }
    public List<string>? ConnectedTools { get; init; }
    public List<JsonElement>? ActiveTasks { get; init; }
}

/// <summary>
/// Per-feature source-gen JSON context for the agent verbs. Each Phase 1 verb
/// folder owns its own context so the source-gen surface stays local and
/// silo response-shape changes don't ripple across the action layer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AgentWire))]
[JsonSerializable(typeof(AgentWire[]))]
[JsonSerializable(typeof(List<AgentWire>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
