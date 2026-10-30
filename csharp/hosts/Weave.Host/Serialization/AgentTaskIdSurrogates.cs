namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct AgentTaskIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
