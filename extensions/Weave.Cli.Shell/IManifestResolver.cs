namespace Weave.Cli.Shell;

internal interface IManifestResolver
{
    /// <summary>
    /// Resolves a workspace name (or null for "current directory") to the
    /// absolute path of its <c>workspace.json</c>, or returns null if no
    /// matching manifest exists. Walks the workspace registry first when a
    /// name is supplied, falling back to the upward CWD search.
    /// </summary>
    string? Resolve(string? workspace);
}
