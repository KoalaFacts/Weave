namespace Weave.Actions.Config;

/// <summary>
/// Result of <see cref="GetConfigAction"/>. <see cref="Summary"/> is always
/// populated. <see cref="RequestedValue"/> is non-null only when the input
/// supplied a recognized key — frontends can render either the whole table
/// (no key) or just the single value (key path) without branching on input.
/// </summary>
public sealed record GetConfigResult
{
    public required ConfigSummary Summary { get; init; }
    public string? RequestedValue { get; init; }
}
