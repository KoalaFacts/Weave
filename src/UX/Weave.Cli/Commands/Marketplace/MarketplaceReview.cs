namespace Weave.Cli.Commands;

internal sealed record MarketplaceReview(string ReviewerId, bool Approved, string? Notes);
