namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct InvocationAttemptIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
