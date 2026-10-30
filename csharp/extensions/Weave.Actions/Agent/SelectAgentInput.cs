namespace Weave.Actions.Agent;

/// <summary>
/// Input for <see cref="SelectAgentAction"/>. The frontend supplies the
/// candidate list (typically from <see cref="ListAgentsAction"/> when the
/// workspace is running, or the manifest's agent map when it isn't); the
/// action validates a requested name or prompts the user to pick one.
/// </summary>
public sealed record SelectAgentInput(IReadOnlyList<string> Candidates, string? RequestedName);
