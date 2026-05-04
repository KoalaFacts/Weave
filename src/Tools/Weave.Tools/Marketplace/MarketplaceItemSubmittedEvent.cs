using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Tools.Marketplace;

public sealed record MarketplaceItemSubmittedEvent : DomainEvent
{
    public required MarketplaceItemId ItemId { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
}
