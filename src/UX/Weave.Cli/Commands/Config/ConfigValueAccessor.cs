using System.Globalization;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class ConfigValueAccessor
{
    public string? GetValue(CliConfig config, string key) => key.ToLowerInvariant() switch
    {
        "version" => config.Version,
        "silopath" => config.SiloPath ?? "(not set)",
        "defaultport" => config.DefaultPort.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    public CliConfig? SetValue(CliConfig config, string key, string value) => key.ToLowerInvariant() switch
    {
        "silopath" => config with { SiloPath = value },
        "defaultport" when int.TryParse(value, CultureInfo.InvariantCulture, out var port) => config with { DefaultPort = port },
        "defaultport" => null,
        _ => null
    };
}
