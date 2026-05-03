using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

public sealed record SkillCreatedEvent : DomainEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required SkillId SkillId { get; init; }
    public required string Title { get; init; }
    public required string CreatedByAgent { get; init; }
}
