using System.Text.Json.Serialization;
using Weave.Tools.Marketplace;
namespace Weave.Silo.Api;

public sealed record SubmitMarketplaceItemRequest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<MarketplaceItemCategory>))]
    public required MarketplaceItemCategory Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public List<string>? Tags { get; init; }
    public List<string>? RequiredCapabilities { get; init; }
    public string? DocumentationUrl { get; init; }
    public string? TemplateId { get; init; }
}
