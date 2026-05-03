namespace Weave.Silo.Serialization;

[GenerateSerializer]
public struct MarketplaceItemIdSurrogate
{
    [Id(0)] public string Value { get; set; }
}
