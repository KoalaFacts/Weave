namespace Weave.Cli.Commands;

internal sealed record ApiMarketplaceItemResponse
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required string Status { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset? PublishedAt { get; init; }
    public int InstallCount { get; init; }
    public double Rating { get; init; }
    public int RatingCount { get; init; }
}
