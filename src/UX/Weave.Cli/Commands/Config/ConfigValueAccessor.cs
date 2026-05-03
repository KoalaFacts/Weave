using System.Globalization;

namespace Weave.Cli.Commands;

internal static class ConfigValueAccessor
{
    public static string? GetValue(CliConfig config, string key) => key.ToLowerInvariant() switch
    {
        "version" => config.Version,
        "silopath" => config.SiloPath ?? "(not set)",
        "defaultport" => config.DefaultPort.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    public static CliConfig? SetValue(CliConfig config, string key, string value) => key.ToLowerInvariant() switch
    {
        "silopath" => config with { SiloPath = value },
        "defaultport" when int.TryParse(value, CultureInfo.InvariantCulture, out var port) => config with { DefaultPort = port },
        "defaultport" => null,
        _ => null
    };
}
