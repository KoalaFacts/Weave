namespace Weave.Dashboard.Services;

public sealed record CapabilityAuditEntryDto
{
    public string TokenId { get; init; } = "";
    public string Grant { get; init; } = "";
    public string IssuedTo { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string ActionContext { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
