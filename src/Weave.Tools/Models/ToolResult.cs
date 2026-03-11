namespace Weave.Tools.Models;

[GenerateSerializer]
public sealed record ToolResult
{
    [Id(0)] public bool Success { get; init; }
    [Id(1)] public string Output { get; init; } = string.Empty;
    [Id(2)] public string? Error { get; init; }
    [Id(3)] public TimeSpan Duration { get; init; }
    [Id(4)] public string ToolName { get; init; } = string.Empty;
}

[GenerateSerializer]
public sealed record ToolInvocation
{
    [Id(0)] public string ToolName { get; init; } = string.Empty;
    [Id(1)] public string Method { get; init; } = string.Empty;
    [Id(2)] public Dictionary<string, string> Parameters { get; init; } = [];
    [Id(3)] public string? RawInput { get; init; }
}

[GenerateSerializer]
public sealed record ToolHandle
{
    [Id(0)] public string ToolName { get; init; } = string.Empty;
    [Id(1)] public ToolType Type { get; init; }
    [Id(2)] public string ConnectionId { get; init; } = string.Empty;
    [Id(3)] public bool IsConnected { get; init; }
}

[GenerateSerializer]
public sealed record ToolSchema
{
    [Id(0)] public string ToolName { get; init; } = string.Empty;
    [Id(1)] public string Description { get; init; } = string.Empty;
    [Id(2)] public List<ToolParameter> Parameters { get; init; } = [];
}

[GenerateSerializer]
public sealed record ToolParameter
{
    [Id(0)] public string Name { get; init; } = string.Empty;
    [Id(1)] public string Type { get; init; } = "string";
    [Id(2)] public string Description { get; init; } = string.Empty;
    [Id(3)] public bool Required { get; init; }
}
