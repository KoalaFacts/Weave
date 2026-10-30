namespace Weave.Actions.AgentTask;

/// <summary>
/// Frontend-friendly view of an agent task returned by
/// <see cref="ListTasksAction"/>. Translated from the silo's wire shape inside
/// the action so the action layer stays oblivious to the wire shape.
/// </summary>
public sealed record TaskSummary
{
    public required string TaskId { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
