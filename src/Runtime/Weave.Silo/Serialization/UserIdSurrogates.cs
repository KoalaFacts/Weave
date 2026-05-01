namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct UserIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
