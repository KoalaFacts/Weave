namespace Weave.Tools.Marketplace;

public sealed record MarketplaceState
{
    public Dictionary<string, MarketplaceItem> Items { get; init; } = [];
}
