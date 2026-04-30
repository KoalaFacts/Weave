using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

public sealed record EpisodeStoredEvent : DomainEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required EpisodeId EpisodeId { get; init; }
    public required string Title { get; init; }
    public required string AgentName { get; init; }
}
