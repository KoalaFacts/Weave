using Weave.Actions.Agent;
using Weave.Actions.Tool;

namespace Weave.Actions.Workspace;

/// <summary>
/// Result of <see cref="WatchWorkspaceAction"/>: a composite live snapshot
/// of the workspace plus its agents and tools. Status is the only required
/// component — when the silo can't return the agent or tool list (e.g. a
/// transient 5xx during a polling tick) those collections are empty rather
/// than failing the whole snapshot, mirroring the Spectre watch-loop's
/// "best-effort" semantics.
/// </summary>
public sealed record WatchWorkspaceResult(
    WorkspaceStatusSummary Workspace,
    IReadOnlyList<AgentSummary> Agents,
    IReadOnlyList<ToolSummary> Tools);
