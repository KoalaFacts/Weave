namespace Weave.Silo.Api;

public sealed record RateMarketplaceItemRequest
{
    public required double Rating { get; init; }
}
