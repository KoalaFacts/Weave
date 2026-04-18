using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

[GenerateSerializer]
public sealed record UserInteractionRecordedEvent : DomainEvent
{
    [Id(3)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(4)] public required string UserId { get; init; }
    [Id(5)] public required string AgentName { get; init; }
}
