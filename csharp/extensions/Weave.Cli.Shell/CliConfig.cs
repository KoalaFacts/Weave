using Weave.Shared;

namespace Weave.Cli.Shell;

internal sealed record CliConfig
{
    public string Version { get; init; } = "1.0";
    public string? SiloPath { get; init; }
    public int DefaultPort { get; init; } = WeavePorts.SiloHttp;
    public string Storage { get; init; } = "memory";
    public string? ConnectionString { get; init; }
    public string AuthMode { get; init; } = "none";
    public string? AuthSecret { get; init; }
    public bool RequireHttps { get; init; }
}
