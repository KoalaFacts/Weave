namespace Weave.Actions.Marketplace;

/// <summary>
/// Curated marketplace item shape for user-facing list/search/info verbs.
/// Distinct from the Phase 4b <see cref="ListMarketplaceItemsResult"/>
/// (opaque <see cref="System.Text.Json.JsonElement"/>) which exists for
/// workspace export wire fidelity.
/// </summary>
public sealed record MarketplaceItemSummary
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset? PublishedAt { get; init; }
    public int InstallCount { get; init; }
    public double Rating { get; init; }
    public int RatingCount { get; init; }
    public string? TemplateId { get; init; }
}
