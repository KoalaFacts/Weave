namespace Weave.Actions.Config;

/// <summary>
/// Curated, frontend-shaped view of the local CLI configuration. Mirrors the
/// rows the CLI's <c>weave config get</c> table already prints. Pre-formatted
/// strings (e.g., <see cref="DefaultPort"/> as decimal, <see cref="RequireHttps"/>
/// as <c>"true"</c>/<c>"false"</c>) so frontends don't redo the formatting.
/// </summary>
public sealed record ConfigSummary
{
    public required string Version { get; init; }
    public required string DefaultPort { get; init; }
    public required string Storage { get; init; }
    public required string AuthMode { get; init; }
    public required string RequireHttps { get; init; }
    public required string SiloPath { get; init; }
    public required string WeaveHome { get; init; }
    public required string BaseUrl { get; init; }
}
