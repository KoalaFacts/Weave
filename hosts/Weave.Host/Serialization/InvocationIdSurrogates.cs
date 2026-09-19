namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct InvocationIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
