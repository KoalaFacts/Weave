using System.Globalization;

namespace Weave.Cli.Commands;

internal static class ConfigValueAccessor
{
    public static CliConfig? SetValue(CliConfig config, string key, string value) => key.ToLowerInvariant() switch
    {
        "silopath" => config with { SiloPath = value },
        "defaultport" when int.TryParse(value, CultureInfo.InvariantCulture, out var port) => config with { DefaultPort = port },
        "defaultport" => null,
        _ => null
    };
}
