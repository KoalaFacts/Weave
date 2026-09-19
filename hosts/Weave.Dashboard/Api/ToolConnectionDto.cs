namespace Weave.Dashboard.Api;

public sealed record ToolConnectionDto
{
    public string ToolName { get; init; } = "";
    public string ToolType { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
