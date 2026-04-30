using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct NetworkIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}

[RegisterConverter]
public sealed class NetworkIdSurrogateConverter : IConverter<NetworkId, NetworkIdSurrogate>
{
    public NetworkId ConvertFromSurrogate(in NetworkIdSurrogate s)
        => NetworkId.From(s.Value ?? string.Empty);

    public NetworkIdSurrogate ConvertToSurrogate(in NetworkId v)
        => new() { Value = v.Value ?? string.Empty };
}
