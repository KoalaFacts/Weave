using Weave.Actions.SystemInfo;

namespace Weave.Cli.Commands;

/// <summary>
/// CLI binding for <see cref="ISystemConfigSource"/>: reads
/// <c>~/.weave/config.json</c> via <see cref="CliConfigStore"/> and folds in the
/// resolved silo base URL plus the conventional Weave home directory.
/// </summary>
internal sealed class CliSystemConfigSource : ISystemConfigSource
{
    public SystemConfigSnapshot Load()
    {
        var config = CliConfigStore.Load();
        return new SystemConfigSnapshot
        {
            BaseUrl = CliApiHttp.ResolveBaseUrl(),
            DefaultPort = config.DefaultPort,
            Storage = config.Storage,
            AuthMode = config.AuthMode,
            RequireHttps = config.RequireHttps,
            SiloPath = string.IsNullOrWhiteSpace(config.SiloPath) ? null : config.SiloPath,
            WeaveHome = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave")
        };
    }
}
