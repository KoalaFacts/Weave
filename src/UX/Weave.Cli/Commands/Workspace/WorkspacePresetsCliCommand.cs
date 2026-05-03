using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePresetsCliCommand : ICliCommand<NoCliOptions>
{
    public string Name => "presets";

    public IReadOnlyList<string> Aliases => ["p"];

    public string Description => "Browse ready-made workspace templates";

    public Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var table = CliTheme.CreateTable("Presets");
        table.AddColumn(CliTheme.StyledColumn("Preset"));
        table.AddColumn(CliTheme.StyledColumn("Description"));
        table.AddColumn(CliTheme.StyledColumn("Model"));
        table.AddColumn(CliTheme.StyledColumn("Tools"));

        foreach (var (name, preset) in WorkspacePresets.All)
        {
            table.AddRow(
                $"[bold]{Markup.Escape(name)}[/]",
                Markup.Escape(preset.Description),
                Markup.Escape(preset.Model),
                preset.Tools.Count > 0
                    ? string.Join(", ", preset.Tools)
                    : $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]none[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("Use weave workspace new <name> --preset <preset> to create a workspace from a preset.");
        return Task.FromResult(0);
    }
}
