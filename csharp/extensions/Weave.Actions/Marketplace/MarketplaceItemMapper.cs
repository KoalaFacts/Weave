namespace Weave.Actions.Marketplace;

internal static class MarketplaceItemMapper
{
    public static MarketplaceItemSummary ToSummary(MarketplaceItemWire w) =>
        new()
        {
            ItemId = w.ItemId,
            Name = w.Name,
            Description = w.Description,
            Category = w.Category,
            Version = w.Version,
            Author = w.Author,
            Status = w.Status,
            Tags = w.Tags ?? [],
            PublishedAt = w.PublishedAt,
            InstallCount = w.InstallCount,
            Rating = w.Rating,
            RatingCount = w.RatingCount,
            TemplateId = w.TemplateId
        };

    public static IReadOnlyList<MarketplaceItemSummary> ToSummaries(IReadOnlyList<MarketplaceItemWire> wires)
    {
        var summaries = new MarketplaceItemSummary[wires.Count];
        for (var i = 0; i < wires.Count; i++)
            summaries[i] = ToSummary(wires[i]);
        return summaries;
    }
}
