namespace Weave.Actions.SystemInfo;

/// <summary>
/// Result of <see cref="GetSystemInfoAction"/>: the local snapshot stitched
/// with the silo reachability probe. Frontends render the fields in whatever
/// shape suits them (table, JSON, badge).
/// </summary>
public sealed record SystemInfoResult
{
    public required bool Reachable { get; init; }
    public required string BaseUrl { get; init; }
    public required int DefaultPort { get; init; }
    public required string Storage { get; init; }
    public required string AuthMode { get; init; }
    public required bool RequireHttps { get; init; }
    public required string? SiloPath { get; init; }
    public required string WeaveHome { get; init; }
}
