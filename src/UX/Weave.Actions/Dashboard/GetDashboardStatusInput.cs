namespace Weave.Actions.Dashboard;

/// <summary>
/// Input for <see cref="GetDashboardStatusAction"/>. <see cref="Url"/> null
/// means "compute the default from local config" (handles the Weave-port
/// convention so frontends don't recompute it). When the frontend already
/// resolved a URL — env var, flag, persisted preference — pass it explicitly.
/// </summary>
public sealed record GetDashboardStatusInput(string? Url = null);
