namespace Weave.Actions.Dashboard;

/// <summary>
/// Result of <see cref="GetDashboardStatusAction"/>. The dashboard probe is
/// always best-effort — transport failures collapse to <c>Reachable = false</c>
/// rather than a structured failure, mirroring how
/// <c>GetSystemInfoAction</c> handles its <c>/health</c> probe.
/// </summary>
public sealed record GetDashboardStatusResult(string Url, bool Reachable);
