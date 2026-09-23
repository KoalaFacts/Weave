using Weave.Actions.SystemInfo;

namespace Weave.Cli.Shell;

/// <summary>
/// CLI binding for <see cref="ISystemConfigSource"/>: reads
/// <c>~/.weave/config.json</c> via <see cref="IConfigStore"/> and folds in the
/// resolved silo base URL plus the conventional Weave home directory.
/// </summary>
internal sealed class CliSystemConfigSource(IConfigStore configStore) : ISystemConfigSource
{
    public SystemConfigSnapshot Load()
    {
        var config = configStore.Load();
        return new SystemConfigSnapshot
        {
            Version = config.Version,
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
