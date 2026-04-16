using Weave.Shared.Ids;

namespace Weave.Agents.Models;

public enum ChannelType { Slack, Discord, Telegram, Teams, Email }

[GenerateSerializer]
public sealed record ChannelConfig
{
    [Id(0)] public required ChannelId ChannelId { get; init; }
    [Id(1)] public required ChannelType Type { get; init; }
    [Id(2)] public required string Name { get; init; }
    [Id(3)] public Dictionary<string, string> Config { get; init; } = [];
    [Id(4)] public string? TargetAgent { get; init; }
    [Id(5)] public bool Enabled { get; init; } = true;
}

[GenerateSerializer]
public sealed record InboundMessage
{
    [Id(0)] public required ChannelId ChannelId { get; init; }
    [Id(1)] public required ChannelType SourceChannel { get; init; }
    [Id(2)] public required string SenderId { get; init; }
    [Id(3)] public required string SenderName { get; init; }
    [Id(4)] public required string Content { get; init; }
    [Id(5)] public string? ThreadId { get; init; }
    [Id(6)] public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(7)] public Dictionary<string, string> Metadata { get; init; } = [];
}

[GenerateSerializer]
public sealed record OutboundMessage
{
    [Id(0)] public required ChannelId ChannelId { get; init; }
    [Id(1)] public required string Content { get; init; }
    [Id(2)] public string? ThreadId { get; init; }
    [Id(3)] public Dictionary<string, string> Metadata { get; init; } = [];
}

[GenerateSerializer]
public sealed record ChannelGatewayState
{
    [Id(0)] public Dictionary<string, ChannelConfig> Channels { get; init; } = [];
    [Id(1)] public Dictionary<string, string> RoutingRules { get; init; } = [];
    [Id(2)] public string WorkspaceId { get; set; } = string.Empty;
}
