namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct AgentIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
