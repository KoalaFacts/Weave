namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct TemplateIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
