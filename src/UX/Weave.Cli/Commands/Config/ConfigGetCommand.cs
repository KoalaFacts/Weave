using System.CommandLine;
using System.Globalization;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class ConfigGetCommand
{
    public static Command Create()
    {
        var keyArg = new Argument<string?>("key")
        {
            Description = "Config key to read (omit to show all)",
            Arity = ArgumentArity.ZeroOrOne
        };
        keyArg.CompletionSources.Add(CliCompletions.CompleteConfigKeys);

        var cmd = new Command("get", "Show configuration values") { keyArg };
        cmd.SetAction(parseResult =>
        {
            var key = parseResult.GetValue(keyArg);
            var config = CliConfigStore.Load();

            if (key is null)
            {
                var table = CliTheme.CreateTable("Configuration");
                table.AddColumn(CliTheme.StyledColumn("Key"));
                table.AddColumn(CliTheme.StyledColumn("Value"));

                table.AddRow("version", config.Version);
                table.AddRow("siloPath", config.SiloPath ?? "(not set)");
                table.AddRow("defaultPort", config.DefaultPort.ToString(CultureInfo.InvariantCulture));

                AnsiConsole.Write(table);
                return 0;
            }

            var value = GetValue(config, key);
            if (value is null)
            {
                CliTheme.WriteError($"Unknown config key '{key}'.");
                CliTheme.WriteMuted("  Valid keys: version, siloPath, defaultPort");
                return 1;
            }

            AnsiConsole.WriteLine(value);
            return 0;
        });

        return cmd;
    }

    internal static string? GetValue(CliConfig config, string key) => key.ToLowerInvariant() switch
    {
        "version" => config.Version,
        "silopath" => config.SiloPath ?? "(not set)",
        "defaultport" => config.DefaultPort.ToString(CultureInfo.InvariantCulture),
        _ => null
    };
}
