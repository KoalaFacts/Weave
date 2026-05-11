namespace Weave.Actions.Config;

/// <summary>
/// Input for <see cref="GetConfigAction"/>. <see cref="Key"/> null returns the
/// whole snapshot; non-null narrows to a single value (matching the existing
/// <c>weave config get [key]</c> CLI shape).
/// </summary>
public sealed record GetConfigInput(string? Key = null);
