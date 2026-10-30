using Weave.Shared.Ids;

namespace Weave.Agents.Channels;

public sealed record OutboundMessage
{
    public required ChannelId ChannelId { get; init; }
    public required string Content { get; init; }
    public string? ThreadId { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}
