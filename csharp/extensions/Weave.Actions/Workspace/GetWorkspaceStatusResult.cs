namespace Weave.Actions.Workspace;

/// <summary>
/// Result of <see cref="GetWorkspaceStatusAction"/>: the live workspace info
/// reported by the silo. Silo unreachable is signalled via
/// <c>ActionFailure</c> with reason <c>SiloUnreachable</c>; a 404 from the
/// silo (workspace id not running) maps to <c>NotFound</c>.
/// </summary>
public sealed record GetWorkspaceStatusResult(WorkspaceStatusSummary Workspace);
