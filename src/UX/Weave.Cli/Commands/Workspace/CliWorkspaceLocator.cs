using Weave.Actions.Workspace;

namespace Weave.Cli.Commands;

/// <summary>
/// CLI binding for <see cref="IWorkspaceLocator"/>. <c>RegisteredNames</c>
/// reads <c>~/.weave/workspaces.json</c> via <see cref="WorkspaceRegistry"/>;
/// <c>ResolveManifestPath</c> walks the same registry first and falls back
/// to the upward CWD search via <see cref="ManifestResolver"/>.
/// </summary>
internal sealed class CliWorkspaceLocator : IWorkspaceLocator
{
    public IReadOnlyList<string> RegisteredNames() => [.. WorkspaceRegistry.GetAll().Keys];

    public string? ResolveManifestPath(string? name) => ManifestResolver.Resolve(name);
}
