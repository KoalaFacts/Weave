namespace Weave.Cli.Commands;

internal sealed record ApiPublishMarketplaceRequest
{
    public required string ReviewerId { get; init; }
    public required bool Approved { get; init; }
    public string? Notes { get; init; }
}
