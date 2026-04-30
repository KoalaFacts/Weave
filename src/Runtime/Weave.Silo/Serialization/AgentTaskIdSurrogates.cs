using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct AgentTaskIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class AgentTaskIdSurrogateConverter : IConverter<AgentTaskId, AgentTaskIdSurrogate>
{
    public AgentTaskId ConvertFromSurrogate(in AgentTaskIdSurrogate s)
        => AgentTaskId.From(s.Value ?? string.Empty);

    public AgentTaskIdSurrogate ConvertToSurrogate(in AgentTaskId v)
        => new() { Value = v.Value ?? string.Empty };
}
