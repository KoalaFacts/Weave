using System.Text.Json.Serialization;

namespace Weave.Actions.AgentTask;

/// <summary>
/// Wire shape of an agent task as returned by the silo's
/// <c>/api/workspaces/{id}/agents/{agentName}/tasks</c> endpoint. Internal to
/// the action because frontends consume the curated <see cref="TaskSummary"/>
/// instead. Kept local so the silo's response shape can change without
/// rippling into a centralized DTO surface.
/// </summary>
internal sealed record TaskWire
{
    public string TaskId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Per-feature source-gen JSON context for the task verbs. Each Phase 1 verb
/// folder owns its own context so the source-gen surface stays local and silo
/// response-shape changes don't ripple across the action layer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TaskWire))]
[JsonSerializable(typeof(TaskWire[]))]
[JsonSerializable(typeof(List<TaskWire>))]
internal sealed partial class TaskJsonContext : JsonSerializerContext;
