using Weave.Shared.Ids;

namespace Weave.Tools.Models;

public enum MarketplaceItemStatus { Draft, PendingReview, Published, Deprecated, Rejected }
public enum MarketplaceItemCategory { ToolConnector, AgentSkill, ToolChain, Integration }
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
public sealed record SecurityReview
{
    public required string ReviewerId { get; init; }
    public required bool Approved { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset ReviewedAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record MarketplaceState
{
    public Dictionary<string, MarketplaceItem> Items { get; init; } = [];
}
