using Weave.Shared.Ids;

namespace Weave.Agents.Models;

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
