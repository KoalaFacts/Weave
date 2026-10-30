namespace Weave.Actions.Agent;

/// <summary>
/// Input for <see cref="SendMessageStreamingAction"/>. Frontends pass the
/// workspace and agent identifiers they already resolved plus the user's
/// message text.
/// </summary>
public sealed record SendMessageInput(string WorkspaceId, string AgentName, string Content);
