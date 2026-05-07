using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Cli.Commands;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiMarketplaceItemResponse))]
[JsonSerializable(typeof(List<ApiMarketplaceItemResponse>))]
[JsonSerializable(typeof(ApiSubmitMarketplaceRequest))]
[JsonSerializable(typeof(ApiPublishMarketplaceRequest))]
[JsonSerializable(typeof(ApiMarketplaceInstallResponse))]
[JsonSerializable(typeof(ApiCapabilityAuditEntry))]
[JsonSerializable(typeof(List<ApiCapabilityAuditEntry>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<JsonElement>))]
internal sealed partial class CliApiJsonContext : JsonSerializerContext;
