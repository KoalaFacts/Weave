namespace Weave.Tools.Models;
public sealed record ToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public string ToolName { get; init; } = string.Empty;
}
public sealed record ToolInvocation
{
    public string ToolName { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = [];
    public string? RawInput { get; init; }
}
public sealed record ToolHandle
{
    public string ToolName { get; init; } = string.Empty;
    public ToolType Type { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public bool IsConnected { get; init; }
}
public sealed record ToolSchema
{
    public string ToolName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<ToolParameter> Parameters { get; init; } = [];
}
public sealed record ToolParameter
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public string Description { get; init; } = string.Empty;
    public bool Required { get; init; }
}
