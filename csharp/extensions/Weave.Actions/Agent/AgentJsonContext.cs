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
/// Wire shape sent to <c>POST /api/workspaces/{id}/agents/{name}/messages</c>.
/// Mirrors the silo's <c>SendMessageRequest</c> contract; <c>role</c> defaults
/// to <c>"user"</c> on the silo side and the frontend never overrides it
/// today.
/// </summary>
internal sealed record SendMessageWire
{
    public required string Content { get; init; }
}

/// <summary>
/// Wire shape returned by the silo's send-message endpoint. Internal because
/// frontends consume the curated <see cref="SendMessageResult"/> /
/// <see cref="ConversationMessage"/> records.
/// </summary>
internal sealed record ChatResponseWire
{
    public string Content { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public bool UsedTools { get; init; }
    public string? Model { get; init; }
    public List<ConversationMessageWire> Messages { get; init; } = [];
}

internal sealed record ConversationMessageWire
{
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Subset of RFC 7807 ProblemDetails the chat verb reads to surface a useful
/// message on 400/409.
/// </summary>
internal sealed record AgentProblemWire
{
    public Dictionary<string, string[]>? Errors { get; init; }
    public string? Detail { get; init; }
    public string? Title { get; init; }
}

/// <summary>
/// Wire shape for the silo's <c>event: text</c> SSE frames on the streaming-chat
/// endpoint. Mirrors <c>Weave.Silo.Api.TextEventWire</c>.
/// </summary>
internal sealed record StreamingTextWire
{
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Per-feature source-gen JSON context for the agent verbs. Each Phase 1 verb
/// folder owns its own context so the source-gen surface stays local and
/// silo response-shape changes don't ripple across the action layer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentWire))]
[JsonSerializable(typeof(AgentWire[]))]
[JsonSerializable(typeof(List<AgentWire>))]
[JsonSerializable(typeof(SendMessageWire))]
[JsonSerializable(typeof(ChatResponseWire))]
[JsonSerializable(typeof(AgentProblemWire))]
[JsonSerializable(typeof(StreamingTextWire))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
