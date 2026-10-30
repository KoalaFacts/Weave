namespace Weave.Actions.Workspace;

/// <summary>
/// Frontend-supplied seam that <see cref="OpenWorkspaceAction"/> uses to
/// enumerate registered workspaces and resolve a name to a manifest path.
/// The CLI implementation reads <c>~/.weave/workspaces.json</c> and walks
/// up from the current directory; a future Web UI would read the same
/// shape from a silo-side endpoint or a per-tenant registry.
/// </summary>
public interface IWorkspaceLocator
{
    /// <summary>
    /// Names of every registered workspace. Empty when no workspace has
    /// been registered on this machine.
    /// </summary>
    IReadOnlyList<string> RegisteredNames();

    /// <summary>
    /// Resolves a workspace name (or the implicit "current directory" when
    /// <paramref name="name"/> is null) to a <c>workspace.json</c> path on
    /// disk, or null if no manifest can be located.
    /// </summary>
    string? ResolveManifestPath(string? name);
}
