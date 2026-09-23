namespace Weave.Actions.Agent;

/// <summary>
/// Result of <see cref="ListAgentsAction"/>: the live agents reported by the
/// silo for the requested workspace. Empty list is a valid success result;
/// silo unreachable is signalled via <c>ActionFailure</c> with reason
/// <c>SiloUnreachable</c>.
/// </summary>
public sealed record ListAgentsResult(IReadOnlyList<AgentSummary> Agents);
