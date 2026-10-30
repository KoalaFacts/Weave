namespace Weave.Actions.SystemInfo;

/// <summary>
/// Static facts about the local Weave installation that the
/// <see cref="GetSystemInfoAction"/> stitches into its result.
/// Frontend-supplied through <see cref="ISystemConfigSource"/> — the CLI
/// reads <c>~/.weave/config.json</c>; a future Web UI implementation can read
/// the same shape from a silo-side endpoint.
/// </summary>
public sealed record SystemConfigSnapshot
{
    public required string Version { get; init; }
    public required string BaseUrl { get; init; }
    public required int DefaultPort { get; init; }
    public required string Storage { get; init; }
    public required string AuthMode { get; init; }
    public required bool RequireHttps { get; init; }
    public required string? SiloPath { get; init; }
    public required string WeaveHome { get; init; }
}
