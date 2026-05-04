using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Workspaces.Templates;

public sealed record TemplateInstantiatedEvent : DomainEvent
{
    public required TemplateId TemplateId { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string AgentName { get; init; }
}
