using Weave.Agents.Models;

namespace Weave.Silo.Api;

public sealed record OutboundMessageResponse
{
    public required string ChannelId { get; init; }
    public required string Content { get; init; }
    public string? ThreadId { get; init; }

    public static OutboundMessageResponse FromMessage(OutboundMessage msg) => new()
    {
        ChannelId = msg.ChannelId.ToString(),
        Content = msg.Content,
        ThreadId = msg.ThreadId
    };
}
