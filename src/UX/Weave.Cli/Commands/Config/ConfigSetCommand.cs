using System.CommandLine;
using System.Globalization;

namespace Weave.Cli.Commands;

internal static class ConfigSetCommand
{
    public static Command Create()
    {
        var keyArg = new Argument<string>("key") { Description = "Config key" };
        keyArg.CompletionSources.Add(CliCompletions.CompleteConfigKeys);
        var valueArg = new Argument<string>("value") { Description = "Config value" };

        var cmd = new Command("set", "Update a configuration value") { keyArg, valueArg };
        cmd.SetAction(parseResult =>
        {
            var key = parseResult.GetValue(keyArg)!;
            var value = parseResult.GetValue(valueArg)!;
            var config = CliConfigStore.Load();

            var updated = SetValue(config, key, value);
            if (updated is null)
            {
                CliTheme.WriteError($"Unknown config key '{key}'.");
                CliTheme.WriteMuted("  Valid keys: siloPath, defaultPort");
                return 1;
            }

            CliConfigStore.Save(updated);
            CliTheme.WriteSuccess($"{key} = {value}");
            return 0;
        });

        return cmd;
    }

    internal static CliConfig? SetValue(CliConfig config, string key, string value) => key.ToLowerInvariant() switch
    {
        "silopath" => config with { SiloPath = value },
        "defaultport" when int.TryParse(value, CultureInfo.InvariantCulture, out var port) => config with { DefaultPort = port },
        "defaultport" => null,
        _ => null
    };
}
