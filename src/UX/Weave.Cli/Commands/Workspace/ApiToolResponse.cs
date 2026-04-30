namespace Weave.Cli.Commands;

internal sealed record ApiToolResponse
{
    public required string ToolName { get; init; }
    public required string ToolType { get; init; }
    public required string Status { get; init; }
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
