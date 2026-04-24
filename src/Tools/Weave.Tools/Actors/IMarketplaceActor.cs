using Weave.Shared.Ids;
using Weave.Tools.Models;

namespace Weave.Tools.Actors;

/// <summary>
/// Actor that manages the curated tool/skill marketplace.
/// Keyed by "global".
/// </summary>
public interface IMarketplaceActor
{
    Task<MarketplaceItem> SubmitAsync(MarketplaceItem item);
    Task<MarketplaceItem> PublishAsync(MarketplaceItemId itemId, SecurityReview review);
    Task<MarketplaceItem?> GetAsync(MarketplaceItemId itemId);
    Task<IReadOnlyList<MarketplaceItem>> SearchAsync(string? query, MarketplaceItemCategory? category, int maxResults = 20);
    Task<IReadOnlyList<MarketplaceItem>> GetPublishedAsync(int offset = 0, int limit = 50);
    Task RateAsync(MarketplaceItemId itemId, double rating);
    Task IncrementInstallCountAsync(MarketplaceItemId itemId);
    Task DeprecateAsync(MarketplaceItemId itemId);
}
