namespace Weave.Tools.Models;

public sealed record ToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public string ToolName { get; init; } = string.Empty;
}