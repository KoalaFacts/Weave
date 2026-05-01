using Weave.Shared.Ids;

namespace Weave.Tools.Models;

public sealed record MarketplaceItem
{
    public required MarketplaceItemId ItemId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required MarketplaceItemCategory Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public MarketplaceItemStatus Status { get; set; } = MarketplaceItemStatus.Draft;
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int InstallCount { get; set; }
    public double Rating { get; set; }
    public int RatingCount { get; set; }
    public List<string> RequiredCapabilities { get; init; } = [];
    public string? DocumentationUrl { get; init; }
    public SecurityReview? SecurityReview { get; set; }
}
