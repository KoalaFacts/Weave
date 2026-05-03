namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct NetworkIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
