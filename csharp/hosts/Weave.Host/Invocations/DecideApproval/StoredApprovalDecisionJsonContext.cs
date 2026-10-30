using System.Text.Json.Serialization;

namespace Weave.Silo.Invocations;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredApprovalDecisionRequest))]
internal sealed partial class StoredApprovalDecisionJsonContext : JsonSerializerContext;
