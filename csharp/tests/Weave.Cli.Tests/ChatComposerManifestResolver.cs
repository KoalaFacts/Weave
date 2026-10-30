namespace Weave.Cli.Tests;

internal sealed class ChatComposerManifestResolver(string? path = null) : IManifestResolver
{
    public string? Resolve(string? workspace) => path;
}
