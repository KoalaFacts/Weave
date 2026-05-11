namespace Weave.Actions.Agent;

/// <summary>
/// Input for <see cref="ListAgentsAction"/>. Frontends pass the workspace id
/// they already know about; the action does not prompt for it.
/// </summary>
public sealed record ListAgentsInput(string WorkspaceId);
