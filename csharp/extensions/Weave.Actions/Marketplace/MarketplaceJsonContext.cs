using System.Text.Json;
using System.Text.Json.Serialization;
using Weave.Workspaces.Manifest;

namespace Weave.Actions.Marketplace;

/// <summary>
/// Wire shape returned by the silo's <c>/api/marketplace</c> endpoints.
/// Mirrors the typed <see cref="MarketplaceItemSummary"/> the action exposes
/// to consumers; isolated here so a silo response-shape change ripples to
/// one folder.
/// </summary>
internal sealed record MarketplaceItemWire
{
    public string ItemId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public List<string>? Tags { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public int InstallCount { get; init; }
    public double Rating { get; init; }
    public int RatingCount { get; init; }
    public string? TemplateId { get; init; }
}

internal sealed record SubmitMarketplaceWire
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

internal sealed record PublishMarketplaceWire
{
    public required string ReviewerId { get; init; }
    public required bool Approved { get; init; }
    public string? Notes { get; init; }
}

internal sealed record InstallMarketplaceWire
{
    public required MarketplaceItemWire Item { get; init; }
    public required InstallTemplateWire Template { get; init; }
}

internal sealed record InstallTemplateWire
{
    public string TemplateId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public List<string>? Tags { get; init; }
    public int InstantiationCount { get; init; }
    public AgentDefinition? AgentDefinition { get; init; }
    public Dictionary<string, ToolDefinition>? RequiredTools { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(JsonElement[]))]
[JsonSerializable(typeof(MarketplaceItemWire))]
[JsonSerializable(typeof(List<MarketplaceItemWire>))]
[JsonSerializable(typeof(MarketplaceItemWire[]))]
[JsonSerializable(typeof(SubmitMarketplaceWire))]
[JsonSerializable(typeof(PublishMarketplaceWire))]
[JsonSerializable(typeof(InstallMarketplaceWire))]
internal sealed partial class MarketplaceJsonContext : JsonSerializerContext;
