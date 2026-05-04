namespace Weave.Tools.Models;

public sealed record ToolHandle
{
    public string ToolName { get; init; } = string.Empty;
    public ToolType Type { get; init; }
    public string ConnectionId { get; init; } = string.Empty;
    public bool IsConnected { get; init; }
}
