namespace Weave.Actions.Tool;

/// <summary>
/// Result of <see cref="ListToolsAction"/>: the tool connections reported by
/// the silo for the requested workspace. Empty list is a valid success result;
/// silo unreachable is signalled via <c>ActionFailure</c> with reason
/// <c>SiloUnreachable</c>.
/// </summary>
public sealed record ListToolsResult(IReadOnlyList<ToolSummary> Tools);
