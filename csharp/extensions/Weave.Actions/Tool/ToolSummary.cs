namespace Weave.Actions.Tool;

/// <summary>
/// Frontend-friendly view of a tool connection returned by
/// <see cref="ListToolsAction"/>. Translated from the silo's wire shape inside
/// the action so the action layer stays oblivious to the wire shape.
/// </summary>
public sealed record ToolSummary
{
    public required string ToolName { get; init; }
    public required string ToolType { get; init; }
    public required string Status { get; init; }
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
