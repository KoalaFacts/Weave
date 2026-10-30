namespace Weave.Actions.AgentTask;

/// <summary>
/// Result of <see cref="ListTasksAction"/>: the active tasks reported by the
/// silo for the requested agent. Empty list is a valid success result; silo
/// unreachable is signalled via <c>ActionFailure</c> with reason
/// <c>SiloUnreachable</c>.
/// </summary>
public sealed record ListTasksResult(IReadOnlyList<TaskSummary> Tasks);
