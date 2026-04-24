using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Shared.VirtualActors;
using Weave.Tools.Actors;
using Weave.Tools.Discovery;
using Weave.Tools.Models;

namespace Weave.Silo.VirtualActors;

public sealed class ToolActorGrain : Grain, IToolActorGrain
{
    private readonly ToolActor _actor;

    public ToolActorGrain(
        IVirtualActorProvider actors,
        IToolDiscoveryService discovery,
        ILeakScanner leakScanner,
        ICapabilityTokenService tokenService,
        ILifecycleManager lifecycleManager,
        IEventBus eventBus,
        ILogger<ToolActor> logger)
    {
        _actor = new ToolActor(actors, discovery, leakScanner, tokenService, lifecycleManager, eventBus, logger);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token) =>
        _actor.ConnectAsync(definition, token);

    public Task DisconnectAsync() => _actor.DisconnectAsync();

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token) =>
        _actor.InvokeAsync(invocation, token);

    public Task<ToolSchema> GetSchemaAsync() => _actor.GetSchemaAsync();
    public Task<ToolHandle?> GetHandleAsync() => _actor.GetHandleAsync();
}

public sealed class MarketplaceActorGrain : Grain, IMarketplaceActorGrain
{
    private readonly MarketplaceActor _actor;

    public MarketplaceActorGrain(
        ILogger<MarketplaceActor> logger,
        IEventBus eventBus,
        TimeProvider timeProvider,
        [PersistentState("marketplace", "Default")] IPersistentState<MarketplaceState> state)
    {
        _actor = new MarketplaceActor(logger, eventBus, timeProvider,
            new OrleansActorState<MarketplaceState>(state));
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
}
