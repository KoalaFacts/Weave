namespace Weave.Cli.Tests;

internal sealed class FixedManifestResolver(string? path) : IManifestResolver
{
    public string? Path { get; set; } = path;

    public string? Resolve(string? workspace) => Path;
}
