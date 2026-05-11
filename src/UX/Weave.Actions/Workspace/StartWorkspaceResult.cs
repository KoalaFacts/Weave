namespace Weave.Actions.Workspace;

/// <summary>
/// Result of <see cref="StartWorkspaceAction"/>: the live workspace info
/// reported by the silo on a successful start. Reuses
/// <see cref="WorkspaceStatusSummary"/> because the silo emits the same
/// <c>WorkspaceResponse</c> shape on 201 (start) as on 200 (status).
/// </summary>
public sealed record StartWorkspaceResult(WorkspaceStatusSummary Workspace);
