using Spectre.Console;
using Spectre.Console.Cli;

namespace Weave.Cli.Commands;

public sealed class WorkspacePresetsCommand : Command<WorkspacePresetsCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Preset");
        table.AddColumn("Description");
        table.AddColumn("Model");
        table.AddColumn("Tools");

        foreach (var (name, preset) in WorkspacePresets.All)
        {
            table.AddRow(
                $"[bold]{name}[/]",
                preset.Description,
                preset.Model,
                preset.Tools.Count > 0 ? string.Join(", ", preset.Tools) : "[dim]none[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Use [bold]weave workspace new <name> --preset <preset>[/] to create a workspace from a preset.");
        return 0;
    }
}
