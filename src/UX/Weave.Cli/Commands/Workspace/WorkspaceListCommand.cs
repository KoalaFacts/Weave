using System.CommandLine;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class WorkspaceListCommand
{
    public static Command Create()
    {
        var cmd = new Command("list", "List all workspaces");
        cmd.SetAction(parseResult =>
        {
            var workspaces = WorkspaceRegistry.GetAll();
            if (workspaces.Count == 0)
            {
                CliTheme.WriteWarning("No workspaces found.");
                return 0;
            }

            var table = CliTheme.CreateTable("Workspaces");
            table.AddColumn(CliTheme.StyledColumn("Name"));
            table.AddColumn(CliTheme.StyledColumn("Path"));
            table.AddColumn(CliTheme.StyledColumn("Status"));

            foreach (var (name, dir) in workspaces)
            {
                var hasManifest = File.Exists(Path.Combine(dir, "workspace.json"));
                var status = hasManifest
                    ? $"[rgb({CliTheme.Success.R},{CliTheme.Success.G},{CliTheme.Success.B})]Ready[/]"
                    : Directory.Exists(dir)
                        ? $"[rgb({CliTheme.Error.R},{CliTheme.Error.G},{CliTheme.Error.B})]Invalid[/]"
                        : $"[rgb({CliTheme.Error.R},{CliTheme.Error.G},{CliTheme.Error.B})]Missing[/]";
                table.AddRow(name, dir, status);
            }

            AnsiConsole.Write(table);
            return 0;
        });

        return cmd;
    }
}
