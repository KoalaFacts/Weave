using Orleans;
using Weave.Shared.Ids;

namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record ToolConnection
{
    [Id(0)] public required string ToolName { get; init; }
    [Id(1)] public required string ToolType { get; init; }
    [Id(2)] public ToolConnectionStatus Status { get; set; } = ToolConnectionStatus.Disconnected;
    [Id(3)] public string? Endpoint { get; set; }
    [Id(4)] public ContainerId? ContainerId { get; set; }
    [Id(5)] public DateTimeOffset? ConnectedAt { get; set; }
    [Id(6)] public string? ErrorMessage { get; set; }
}

public enum ToolConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}
