using System.Globalization;
using Spectre.Console;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal sealed class PortsCliCommand : ICliCommand<NoCliOptions>
{
    public string Name => "ports";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show default port assignments";

    public Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
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

        return Task.FromResult(0);
    }
}
