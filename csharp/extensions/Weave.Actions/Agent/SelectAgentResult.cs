namespace Weave.Actions.Agent;

/// <summary>
/// Result of <see cref="SelectAgentAction"/>: the chosen agent name in its
/// canonical casing as it appears in the candidate list. The frontend
/// stores this in its session / sends it as the agent argument on later
/// calls.
/// </summary>
public sealed record SelectAgentResult(string AgentName);
