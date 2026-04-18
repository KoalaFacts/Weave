using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Tools.Events;

[GenerateSerializer]
public sealed record MarketplaceItemSubmittedEvent : DomainEvent
{
    [Id(3)] public required MarketplaceItemId ItemId { get; init; }
    [Id(4)] public required string Name { get; init; }
    [Id(5)] public required string Author { get; init; }
}

[GenerateSerializer]
public sealed record MarketplaceItemPublishedEvent : DomainEvent
{
    [Id(3)] public required MarketplaceItemId ItemId { get; init; }
    [Id(4)] public required string Name { get; init; }
}

[GenerateSerializer]
public sealed record MarketplaceItemDeprecatedEvent : DomainEvent
{
    [Id(3)] public required MarketplaceItemId ItemId { get; init; }
    [Id(4)] public required string Name { get; init; }
}
