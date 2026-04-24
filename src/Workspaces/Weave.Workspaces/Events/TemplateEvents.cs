using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Workspaces.Events;

public sealed record TemplateRegisteredEvent : DomainEvent
{
    public required TemplateId TemplateId { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
}
public sealed record TemplatePublishedEvent : DomainEvent
{
    public required TemplateId TemplateId { get; init; }
    public required string Name { get; init; }
}
public sealed record TemplateInstantiatedEvent : DomainEvent
{
    public required TemplateId TemplateId { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string AgentName { get; init; }
}
