namespace Weave.Actions.Tool;

/// <summary>
/// Input for <see cref="ListToolsAction"/>. Frontends pass the workspace id
/// they already know about; the action does not prompt for it.
/// </summary>
public sealed record ListToolsInput(string WorkspaceId);
