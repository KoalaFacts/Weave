namespace Weave.Actions.Audit;

public sealed record CapabilityAuditEntry
{
    public required string TokenId { get; init; }
    public required string Grant { get; init; }
    public required string IssuedTo { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ActionContext { get; init; }
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
