using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Tools.Events;
public sealed record MarketplaceItemSubmittedEvent : DomainEvent
{
    public required MarketplaceItemId ItemId { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
}
public sealed record MarketplaceItemPublishedEvent : DomainEvent
{
    public required MarketplaceItemId ItemId { get; init; }
    public required string Name { get; init; }
}
public sealed record MarketplaceItemDeprecatedEvent : DomainEvent
{
    public required MarketplaceItemId ItemId { get; init; }
    public required string Name { get; init; }
}
