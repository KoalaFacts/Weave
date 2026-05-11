namespace Weave.Tools.Tool;

public sealed record ToolInvocation
{
    public string ToolName { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = [];
    public string? RawInput { get; init; }
    public string? ParseWarning { get; init; }
}
