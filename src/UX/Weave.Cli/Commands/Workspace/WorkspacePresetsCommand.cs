using System.CommandLine;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class WorkspacePresetsCommand
{
    public static Command Create()
    {
        var cmd = new Command("presets", "Browse ready-made workspace templates");
        cmd.SetAction(parseResult =>
        {
            var table = CliTheme.CreateTable("Presets");
            table.AddColumn(CliTheme.StyledColumn("Preset"));
            table.AddColumn(CliTheme.StyledColumn("Description"));
            table.AddColumn(CliTheme.StyledColumn("Model"));
            table.AddColumn(CliTheme.StyledColumn("Tools"));

            foreach (var (name, preset) in WorkspacePresets.All)
            {
                table.AddRow(
                    $"[bold]{name}[/]",
                    preset.Description,
                    preset.Model,
                    preset.Tools.Count > 0
                        ? string.Join(", ", preset.Tools)
                        : $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]none[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("Use weave workspace new <name> --preset <preset> to create a workspace from a preset.");
            return 0;
        });

        return cmd;
    }
}
