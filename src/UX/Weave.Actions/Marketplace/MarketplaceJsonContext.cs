using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Actions.Marketplace;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(JsonElement[]))]
internal sealed partial class MarketplaceJsonContext : JsonSerializerContext;
