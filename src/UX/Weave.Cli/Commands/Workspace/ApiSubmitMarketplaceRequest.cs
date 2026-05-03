namespace Weave.Cli.Commands;

internal sealed record ApiSubmitMarketplaceRequest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public List<string> Tags { get; init; } = [];
}
