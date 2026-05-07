namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="WatchWorkspaceAction"/>. The frontend passes the
/// running workspace ID it already resolved; the action fetches the
/// composite snapshot (status + agents + tools) per call. Frontends drive
/// the polling loop themselves — the action is single-shot so callers can
/// shape their own cadence (Spectre live display, dashboard SignalR push,
/// CI snapshot, etc.).
/// </summary>
public sealed record WatchWorkspaceInput(string WorkspaceId);
