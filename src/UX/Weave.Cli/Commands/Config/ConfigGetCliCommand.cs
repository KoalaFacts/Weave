using System.Globalization;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class ConfigGetCliCommand : ICliCommand<ConfigGetOptions>
{
    public string Name => "get";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show configuration values";

    public Task<int> ExecuteAsync(ConfigGetOptions options, CancellationToken ct)
    {
        var config = CliConfigStore.Load();

        if (options.Key is null)
        {
            var table = CliTheme.CreateTable("Configuration");
            table.AddColumn(CliTheme.StyledColumn("Key"));
            table.AddColumn(CliTheme.StyledColumn("Value"));

            table.AddRow("version", config.Version);
            table.AddRow("siloPath", config.SiloPath ?? "(not set)");
            table.AddRow("defaultPort", config.DefaultPort.ToString(CultureInfo.InvariantCulture));

            AnsiConsole.Write(table);
            return Task.FromResult(0);
        }

        var value = ConfigGetCommand.GetValue(config, options.Key);
        if (value is null)
        {
            CliTheme.WriteError($"Unknown config key '{options.Key}'.");
            CliTheme.WriteMuted("  Valid keys: version, siloPath, defaultPort");
            return Task.FromResult(1);
        }

        AnsiConsole.WriteLine(value);
        return Task.FromResult(0);
    }
}
