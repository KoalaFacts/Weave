using System.Text.Json.Serialization;
using Weave.Tools.Models;

namespace Weave.Silo.Api;

public sealed record MarketplaceItemResponse
{
    public required string ItemId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<MarketplaceItemCategory>))]
    public required MarketplaceItemCategory Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<MarketplaceItemStatus>))]
    public required MarketplaceItemStatus Status { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset? PublishedAt { get; init; }
    public int InstallCount { get; init; }
    public double Rating { get; init; }
    public int RatingCount { get; init; }
    public string? TemplateId { get; init; }

    public static MarketplaceItemResponse FromItem(MarketplaceItem item) => new()
    {
        ItemId = item.ItemId.ToString(),
        Name = item.Name,
        Description = item.Description,
        Category = item.Category,
        Version = item.Version,
        Author = item.Author,
        Status = item.Status,
        Tags = [.. item.Tags],
        PublishedAt = item.PublishedAt,
        InstallCount = item.InstallCount,
        Rating = item.Rating,
        RatingCount = item.RatingCount,
        TemplateId = item.TemplateId?.ToString()
    };
}
