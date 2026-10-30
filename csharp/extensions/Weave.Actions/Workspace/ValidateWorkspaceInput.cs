namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="ValidateWorkspaceAction"/>. The action reads the
/// manifest from disk; frontends pass the path they already resolved.
/// </summary>
public sealed record ValidateWorkspaceInput(string ManifestPath);
