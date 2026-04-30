using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct ChannelIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class ChannelIdSurrogateConverter : IConverter<ChannelId, ChannelIdSurrogate>
{
    public ChannelId ConvertFromSurrogate(in ChannelIdSurrogate s)
        => ChannelId.From(s.Value ?? string.Empty);

    public ChannelIdSurrogate ConvertToSurrogate(in ChannelId v)
        => new() { Value = v.Value ?? string.Empty };
}
