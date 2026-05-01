using System.Text.Json.Serialization;

namespace Weave.Agents.Actors;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<ProofConditionResultDto>))]
internal sealed partial class ProofValidatorJsonContext : JsonSerializerContext;
