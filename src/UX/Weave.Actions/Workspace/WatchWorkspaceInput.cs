namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="WatchWorkspaceAction"/>: the running workspace ID
/// the frontend already resolved.
/// </summary>
/// <remarks>
/// The action is single-shot — fetches the composite snapshot (status +
/// agents + tools) per call. Frontends drive the polling loop themselves
/// so callers can shape their own cadence (Spectre live display, dashboard
/// SignalR push, CI snapshot, etc.).
/// </remarks>
public sealed record WatchWorkspaceInput(string WorkspaceId);
