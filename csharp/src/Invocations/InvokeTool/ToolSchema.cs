namespace Weave.Tools.Tool;

public sealed record ToolSchema
{
    public string ToolName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<ToolParameter> Parameters { get; init; } = [];
}
