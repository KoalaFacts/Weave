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
        cmd.SetAction((parseResult, cancellationToken) =>
        {
            var key = parseResult.GetValue(keyArg)!;
            var value = parseResult.GetValue(valueArg)!;
            return new ConfigSetCliCommand().ExecuteAsync(new ConfigSetOptions(key, value), cancellationToken);
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
