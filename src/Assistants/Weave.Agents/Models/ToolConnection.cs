using Weave.Shared.Ids;

namespace Weave.Agents.Models;
public sealed record ToolConnection
{
    public required string ToolName { get; init; }
    public required string ToolType { get; init; }
    public ToolConnectionStatus Status { get; set; } = ToolConnectionStatus.Disconnected;
    public string? Endpoint { get; set; }
    public ContainerId? ContainerId { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum ToolConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}
