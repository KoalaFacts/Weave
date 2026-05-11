namespace Weave.Tools.Tool;

public sealed record ToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public string ToolName { get; init; } = string.Empty;
}
