using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

public sealed record ChannelMessageReceivedEvent : DomainEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required ChannelId ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string AgentName { get; init; }
}
