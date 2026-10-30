namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="GetWorkspaceStatusAction"/>. Frontends pass the
/// workspace id they already know about; the action does not prompt for it.
/// </summary>
public sealed record GetWorkspaceStatusInput(string WorkspaceId);
