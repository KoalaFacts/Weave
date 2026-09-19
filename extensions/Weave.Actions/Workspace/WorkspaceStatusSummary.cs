namespace Weave.Actions.Workspace;

/// <summary>
/// Frontend-friendly view of a live workspace returned by
/// <see cref="GetWorkspaceStatusAction"/>. Translated from the silo's wire
/// shape inside the action so the action layer stays oblivious to the wire
/// shape.
/// </summary>
public sealed record WorkspaceStatusSummary
{
    public required string WorkspaceId { get; init; }
    public string? Name { get; init; }
    public required string Status { get; init; }
    public required int ContainerCount { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public string? ErrorMessage { get; init; }
}
