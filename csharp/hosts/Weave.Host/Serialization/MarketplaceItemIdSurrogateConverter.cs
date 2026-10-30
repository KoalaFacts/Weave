using Weave.Shared.Ids;

namespace Weave.Silo.Serialization;

[RegisterConverter]
public sealed class MarketplaceItemIdSurrogateConverter : IConverter<MarketplaceItemId, MarketplaceItemIdSurrogate>
{
    public MarketplaceItemId ConvertFromSurrogate(in MarketplaceItemIdSurrogate s)
        => MarketplaceItemId.From(s.Value ?? string.Empty);

    public MarketplaceItemIdSurrogate ConvertToSurrogate(in MarketplaceItemId v)
        => new() { Value = v.Value ?? string.Empty };
}
