namespace Weave.Actions.AgentTask;

/// <summary>
/// Input for <see cref="ListTasksAction"/>. Frontends pass the workspace id
/// and agent name; the action does not prompt for them.
/// </summary>
public sealed record ListTasksInput(string WorkspaceId, string AgentName);
