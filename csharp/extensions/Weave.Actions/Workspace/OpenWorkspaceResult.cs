namespace Weave.Actions.Workspace;

/// <summary>
/// Result of <see cref="OpenWorkspaceAction"/>: the canonical-case
/// workspace name and the resolved <c>workspace.json</c> path.
/// Frontends use these to open their session and read the manifest.
/// </summary>
public sealed record OpenWorkspaceResult(string Name, string ManifestPath);
