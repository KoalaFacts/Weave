using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Tools.Marketplace;

public sealed record MarketplaceItemDeprecatedEvent : DomainEvent
{
    public required MarketplaceItemId ItemId { get; init; }
    public required string Name { get; init; }
}
