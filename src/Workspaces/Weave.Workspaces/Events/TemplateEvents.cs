using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Workspaces.Events;

[GenerateSerializer]
public sealed record TemplateRegisteredEvent : DomainEvent
{
    [Id(3)] public required TemplateId TemplateId { get; init; }
    [Id(4)] public required string Name { get; init; }
    [Id(5)] public required string Author { get; init; }
}

[GenerateSerializer]
public sealed record TemplatePublishedEvent : DomainEvent
{
    [Id(3)] public required TemplateId TemplateId { get; init; }
    [Id(4)] public required string Name { get; init; }
}

[GenerateSerializer]
public sealed record TemplateInstantiatedEvent : DomainEvent
{
    [Id(3)] public required TemplateId TemplateId { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required string AgentName { get; init; }
}
