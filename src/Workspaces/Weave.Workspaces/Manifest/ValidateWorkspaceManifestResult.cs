namespace Weave.Workspaces.Manifest;

/// <summary>
/// Outcome of <see cref="ValidateWorkspaceManifestQuery"/>: the manifest's
/// structural summary plus the list of validation errors. An empty
/// <see cref="Errors"/> collection means the manifest is valid.
/// </summary>
public sealed record ValidateWorkspaceManifestResult(
    string Name,
    int AgentCount,
    int ToolCount,
    int TargetCount,
    IReadOnlyList<string> Errors);
