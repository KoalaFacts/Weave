namespace Weave.Tools.Models;

public sealed record MarketplaceState
{
    public Dictionary<string, MarketplaceItem> Items { get; init; } = [];
}