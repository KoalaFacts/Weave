using System.Text.Json.Serialization;

namespace Weave.Agents.Verification;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<ProofConditionResultDto>))]
internal sealed partial class ProofValidatorJsonContext : JsonSerializerContext;
