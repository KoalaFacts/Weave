using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Tools.Actors;
using Weave.Tools.Models;

namespace Weave.Silo.VirtualActors;

public sealed class MarketplaceActorGrain : Grain, IMarketplaceActorGrain
{
    private readonly MarketplaceActor _actor;

    public MarketplaceActorGrain(
        ILogger<MarketplaceActor> logger,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ICapabilityAuthorizer authorizer,
        IVirtualActorProvider actors,
        [PersistentState("marketplace", "Default")] IPersistentState<MarketplaceState> state)
    {
        _actor = new MarketplaceActor(logger, eventBus, timeProvider,
            new OrleansActorState<MarketplaceState>(state),
            authorizer,
            actors);
    }

    public Task<MarketplaceItem> SubmitAsync(MarketplaceItem item) => _actor.SubmitAsync(item);

    public Task<MarketplaceItem> PublishAsync(MarketplaceItemId itemId, SecurityReview review) =>
        _actor.PublishAsync(itemId, review);

    public Task<MarketplaceItem?> GetAsync(MarketplaceItemId itemId) => _actor.GetAsync(itemId);

    public Task<IReadOnlyList<MarketplaceItem>> SearchAsync(string? query, MarketplaceItemCategory? category, int maxResults = 20) =>
        _actor.SearchAsync(query, category, maxResults);

    public Task<IReadOnlyList<MarketplaceItem>> GetPublishedAsync(int offset = 0, int limit = 50) =>
        _actor.GetPublishedAsync(offset, limit);

    public Task RateAsync(MarketplaceItemId itemId, double rating) => _actor.RateAsync(itemId, rating);
    public Task IncrementInstallCountAsync(MarketplaceItemId itemId) => _actor.IncrementInstallCountAsync(itemId);
    public Task DeprecateAsync(MarketplaceItemId itemId) => _actor.DeprecateAsync(itemId);

    public Task<MarketplaceInstallResult> InstallAsync(MarketplaceItemId itemId, CapabilityToken token) =>
        _actor.InstallAsync(itemId, token);
}
