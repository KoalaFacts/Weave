namespace Weave.Cli.Commands;

internal sealed record ApiCapabilityAuditEntry
{
    public string TokenId { get; init; } = string.Empty;
    public string Grant { get; init; } = string.Empty;
    public string IssuedTo { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string ActionContext { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
