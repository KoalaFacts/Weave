using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct SkillIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class SkillIdSurrogateConverter : IConverter<SkillId, SkillIdSurrogate>
{
    public SkillId ConvertFromSurrogate(in SkillIdSurrogate s)
        => SkillId.From(s.Value ?? string.Empty);

    public SkillIdSurrogate ConvertToSurrogate(in SkillId v)
        => new() { Value = v.Value ?? string.Empty };
}
