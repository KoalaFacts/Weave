using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[RegisterConverter]
public sealed class AgentIdSurrogateConverter : IConverter<AgentId, AgentIdSurrogate>
{
    public AgentId ConvertFromSurrogate(in AgentIdSurrogate s)
        => AgentId.From(s.Value ?? string.Empty);

    public AgentIdSurrogate ConvertToSurrogate(in AgentId v)
        => new() { Value = v.Value ?? string.Empty };
}
