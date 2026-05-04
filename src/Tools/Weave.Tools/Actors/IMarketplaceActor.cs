using Weave.Security.Tokens;
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

    /// <summary>
    /// Capability-gated install path: validates the <c>marketplace:install</c>
    /// grant on <paramref name="token"/>, resolves the linked template, and
    /// records the install + instantiation on both the marketplace item and
    /// the template. Returns the resolved <see cref="CapabilityTemplate"/> for
    /// the caller to compose a workspace from.
    /// </summary>
    Task<MarketplaceInstallResult> InstallAsync(MarketplaceItemId itemId, CapabilityToken token);
}
