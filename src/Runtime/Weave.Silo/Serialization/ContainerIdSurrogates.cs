namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct ContainerIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
