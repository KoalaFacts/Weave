namespace Weave.Tools.Tool;

public sealed record ToolParameter
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public string Description { get; init; } = string.Empty;
    public bool Required { get; init; }
}
