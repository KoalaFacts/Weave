using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[RegisterConverter]
public sealed class NetworkIdSurrogateConverter : IConverter<NetworkId, NetworkIdSurrogate>
{
    public NetworkId ConvertFromSurrogate(in NetworkIdSurrogate s)
        => NetworkId.From(s.Value ?? string.Empty);

    public NetworkIdSurrogate ConvertToSurrogate(in NetworkId v)
        => new() { Value = v.Value ?? string.Empty };
}
