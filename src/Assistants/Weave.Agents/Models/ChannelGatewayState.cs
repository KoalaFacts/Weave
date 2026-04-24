using Weave.Shared.Ids;

namespace Weave.Agents.Models;

public enum ChannelType { Slack, Discord, Telegram, Teams, Email }
public sealed record ChannelConfig
{
    public required ChannelId ChannelId { get; init; }
    public required ChannelType Type { get; init; }
    public required string Name { get; init; }
    public Dictionary<string, string> Config { get; init; } = [];
    public string? TargetAgent { get; init; }
    public bool Enabled { get; init; } = true;
}
public sealed record InboundMessage
{
    public required ChannelId ChannelId { get; init; }
    public required ChannelType SourceChannel { get; init; }
    public required string SenderId { get; init; }
    public required string SenderName { get; init; }
    public required string Content { get; init; }
    public string? ThreadId { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = [];
}
public sealed record OutboundMessage
{
    public required ChannelId ChannelId { get; init; }
    public required string Content { get; init; }
    public string? ThreadId { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}
public sealed record ChannelGatewayState
{
    public Dictionary<string, ChannelConfig> Channels { get; init; } = [];
    public Dictionary<string, string> RoutingRules { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
}
