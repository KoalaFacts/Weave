using System.Text.Json.Serialization;

namespace Weave.Silo.Invocations;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApprovalDecisionHttpRequest))]
internal sealed partial class ApprovalDecisionJsonContext : JsonSerializerContext;
