using System.Text.Json.Serialization;

namespace Weave.Agents.Grains;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<ProofConditionResultDto>))]
internal sealed partial class ProofValidatorJsonContext : JsonSerializerContext;

internal sealed record ProofConditionResultDto
{
    public string? ConditionName { get; init; }
    public bool Passed { get; init; }
    public string? Detail { get; init; }
}
