using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Tools.Events;
using Weave.Tools.Models;

namespace Weave.Tools.Actors;

public sealed class MarketplaceActor(
    ILogger<MarketplaceActor> logger,
    IEventBus eventBus,
    TimeProvider timeProvider,
    [PersistentState("marketplace", "Default")] IPersistentState<MarketplaceState> persistentState) : VirtualActor, IMarketplaceActor
{
    public async Task<MarketplaceItem> SubmitAsync(MarketplaceItem item)
    {
        item.Status = MarketplaceItemStatus.Draft;
        var key = item.ItemId.ToString();
        persistentState.State.Items[key] = item;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new MarketplaceItemSubmittedEvent
        {
            SourceId = key,
            ItemId = item.ItemId,
            Name = item.Name,
            Author = item.Author
        }, CancellationToken.None);

        logger.LogInformation("Marketplace item {ItemId} ({Name}) submitted by {Author}",
            item.ItemId, item.Name, item.Author);

        return item;
    }

    public async Task<MarketplaceItem> PublishAsync(MarketplaceItemId itemId, SecurityReview review)
    {
        var key = itemId.ToString();
        if (!persistentState.State.Items.TryGetValue(key, out var item))
            throw new KeyNotFoundException($"Marketplace item '{itemId}' not found");

        if (!review.Approved)
            throw new InvalidOperationException("Cannot publish an item without an approved security review");

        item.Status = MarketplaceItemStatus.Published;
        item.PublishedAt = timeProvider.GetUtcNow();
        item.SecurityReview = review;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new MarketplaceItemPublishedEvent
        {
            SourceId = key,
            ItemId = itemId,
            Name = item.Name
        }, CancellationToken.None);

        logger.LogInformation("Marketplace item {ItemId} ({Name}) published", itemId, item.Name);

        return item;
    }

    public Task<MarketplaceItem?> GetAsync(MarketplaceItemId itemId)
    {
        persistentState.State.Items.TryGetValue(itemId.ToString(), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<MarketplaceItem>> SearchAsync(string? query, MarketplaceItemCategory? category, int maxResults = 20)
    {
        var published = persistentState.State.Items.Values
            .Where(i => i.Status == MarketplaceItemStatus.Published);

        if (category.HasValue)
            published = published.Where(i => i.Category == category.Value);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var queryTokens = Tokenize(query);
            if (queryTokens.Length > 0)
            {
                published = published.Where(i =>
                {
                    var nameTokens = new HashSet<string>(Tokenize(i.Name), StringComparer.OrdinalIgnoreCase);
                    var descTokens = new HashSet<string>(Tokenize(i.Description), StringComparer.OrdinalIgnoreCase);
                    var tagTokens = i.Tags.SelectMany(t => Tokenize(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    return queryTokens.Any(qt =>
                        nameTokens.Contains(qt) || descTokens.Contains(qt) || tagTokens.Contains(qt));
                });
            }
        }

        IReadOnlyList<MarketplaceItem> results = published
            .Take(maxResults)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<MarketplaceItem>> GetPublishedAsync(int offset = 0, int limit = 50)
    {
        IReadOnlyList<MarketplaceItem> results = persistentState.State.Items.Values
            .Where(i => i.Status == MarketplaceItemStatus.Published)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult(results);
    }

    public async Task RateAsync(MarketplaceItemId itemId, double rating)
    {
        var key = itemId.ToString();
        if (!persistentState.State.Items.TryGetValue(key, out var item))
            throw new KeyNotFoundException($"Marketplace item '{itemId}' not found");

        item.Rating = (item.Rating * item.RatingCount + rating) / (item.RatingCount + 1);
        item.RatingCount++;
        await persistentState.WriteStateAsync();
    }

    public async Task IncrementInstallCountAsync(MarketplaceItemId itemId)
    {
        var key = itemId.ToString();
        if (!persistentState.State.Items.TryGetValue(key, out var item))
            throw new KeyNotFoundException($"Marketplace item '{itemId}' not found");

        item.InstallCount++;
        await persistentState.WriteStateAsync();
    }

    public async Task DeprecateAsync(MarketplaceItemId itemId)
    {
        var key = itemId.ToString();
        if (!persistentState.State.Items.TryGetValue(key, out var item))
            throw new KeyNotFoundException($"Marketplace item '{itemId}' not found");

        item.Status = MarketplaceItemStatus.Deprecated;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new MarketplaceItemDeprecatedEvent
        {
            SourceId = key,
            ItemId = itemId,
            Name = item.Name
        }, CancellationToken.None);

        logger.LogInformation("Marketplace item {ItemId} ({Name}) deprecated", itemId, item.Name);
    }

    internal static string[] Tokenize(string text)
    {
        return text.Split([' ', ',', '.', ';', ':', '-', '_', '/', '\\', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToArray();
    }
}
