using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

[GenerateSerializer]
public sealed record ChannelMessageReceivedEvent : DomainEvent
{
    [Id(3)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(4)] public required ChannelId ChannelId { get; init; }
    [Id(5)] public required string SenderId { get; init; }
    [Id(6)] public required string AgentName { get; init; }
}

[GenerateSerializer]
public sealed record ChannelMessageSentEvent : DomainEvent
{
    [Id(3)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(4)] public required ChannelId ChannelId { get; init; }
    [Id(5)] public required string AgentName { get; init; }
}

[GenerateSerializer]
public sealed record ChannelRegisteredEvent : DomainEvent
{
    [Id(3)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(4)] public required ChannelId ChannelId { get; init; }
    [Id(5)] public required ChannelType Type { get; init; }
    [Id(6)] public required string Name { get; init; }
}
