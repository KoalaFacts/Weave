namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct SkillIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
