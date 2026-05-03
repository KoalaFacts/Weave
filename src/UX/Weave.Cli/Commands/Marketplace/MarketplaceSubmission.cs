namespace Weave.Cli.Commands;

internal sealed record MarketplaceSubmission(
    string Name,
    string Description,
    string Category,
    string Version,
    string Author,
    string[] Tags);
