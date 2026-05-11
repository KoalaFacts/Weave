using Weave.Actions.Workspace;

namespace Weave.Cli.Shell;

internal sealed class CliWorkspaceLocator(IWorkspaceRegistry registry, IManifestResolver manifestResolver) : IWorkspaceLocator
{
    public IReadOnlyList<string> RegisteredNames() => [.. registry.GetAll().Keys];

    public string? ResolveManifestPath(string? name) => manifestResolver.Resolve(name);
}
