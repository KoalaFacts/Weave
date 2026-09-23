namespace Weave.Silo.Api;

public sealed record PublishMarketplaceItemRequest
{
    public required string ReviewerId { get; init; }
    public required bool Approved { get; init; }
    public string? Notes { get; init; }
}
