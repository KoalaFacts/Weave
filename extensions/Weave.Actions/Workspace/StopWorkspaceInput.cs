namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="StopWorkspaceAction"/>. Frontends pass the workspace
/// id they recorded when the workspace was started; the action does not
/// resolve names.
/// </summary>
public sealed record StopWorkspaceInput(string WorkspaceId);
