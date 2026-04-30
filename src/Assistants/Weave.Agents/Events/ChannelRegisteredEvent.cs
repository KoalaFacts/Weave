using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

public sealed record ChannelRegisteredEvent : DomainEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required ChannelId ChannelId { get; init; }
    public required ChannelType Type { get; init; }
    public required string Name { get; init; }
}
