using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal static class PortsCommand
{
    public static Command Create()
    {
        var cmd = new Command("ports", "Show default port assignments");
        cmd.SetAction(parseResult =>
        {
            var table = CliTheme.CreateTable("Port Assignments");
            table.AddColumn(CliTheme.StyledColumn("Port"));
            table.AddColumn(CliTheme.StyledColumn("Name"));
            table.AddColumn(CliTheme.StyledColumn("Description"));

            foreach (var (name, port, description) in WeavePorts.All)
            {
                table.AddRow(
                    port.ToString(CultureInfo.InvariantCulture),
                    name,
                    description);
            }

            AnsiConsole.Write(table);

            var config = CliConfigStore.Load();
            if (config.DefaultPort != WeavePorts.SiloHttp)
            {
                AnsiConsole.WriteLine();
                CliTheme.WriteInfo($"Config override: defaultPort = {config.DefaultPort.ToString(CultureInfo.InvariantCulture)}");
            }

            return 0;
        });

        return cmd;
    }
}
