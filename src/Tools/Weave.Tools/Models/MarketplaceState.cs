using Weave.Shared.Ids;

namespace Weave.Tools.Models;

public enum MarketplaceItemStatus { Draft, PendingReview, Published, Deprecated, Rejected }
public enum MarketplaceItemCategory { ToolConnector, AgentSkill, ToolChain, Integration }

[GenerateSerializer]
public sealed record MarketplaceItem
{
    [Id(0)] public required MarketplaceItemId ItemId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public required string Description { get; init; }
    [Id(3)] public required MarketplaceItemCategory Category { get; init; }
    [Id(4)] public required string Version { get; init; }
    [Id(5)] public required string Author { get; init; }
    [Id(6)] public MarketplaceItemStatus Status { get; set; } = MarketplaceItemStatus.Draft;
    [Id(7)] public List<string> Tags { get; init; } = [];
    [Id(8)] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(9)] public DateTimeOffset? PublishedAt { get; set; }
    [Id(10)] public int InstallCount { get; set; }
    [Id(11)] public double Rating { get; set; }
    [Id(12)] public int RatingCount { get; set; }
    [Id(13)] public List<string> RequiredCapabilities { get; init; } = [];
    [Id(14)] public string? DocumentationUrl { get; init; }
    [Id(15)] public SecurityReview? SecurityReview { get; set; }
}

[GenerateSerializer]
public sealed record SecurityReview
{
    [Id(0)] public required string ReviewerId { get; init; }
    [Id(1)] public required bool Approved { get; init; }
    [Id(2)] public string? Notes { get; init; }
    [Id(3)] public DateTimeOffset ReviewedAt { get; init; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed record MarketplaceState
{
    [Id(0)] public Dictionary<string, MarketplaceItem> Items { get; init; } = [];
}
