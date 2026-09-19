using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Workspaces.Templates;

public sealed record TemplateRegisteredEvent : DomainEvent
{
    public required TemplateId TemplateId { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
}
