using Spectre.Console;
using Weave.Actions.Tool;
using Weave.Cli.Tui;

namespace Weave.Cli.Commands;

/// <summary>
/// Shared Spectre rendering for <see cref="ListToolsResult"/>. Both the
/// <see cref="ToolsCliCommand"/> and the migrated TUI <c>/tools</c> slash
/// bottom out here so the table layout is owned in one place.
/// </summary>
internal static class ToolsRenderer
{
    public static void RenderLive(string workspaceName, IReadOnlyList<ToolSummary> tools)
    {
        var table = CliTheme.CreateTable($"Tools · {workspaceName}");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Type"));
        table.AddColumn(CliTheme.StyledColumn("Status"));
        table.AddColumn(CliTheme.StyledColumn("Endpoint"));

        foreach (var tool in tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
        {
            table.AddRow(
                $"[bold white]{Markup.Escape(tool.ToolName)}[/]",
                Markup.Escape(tool.ToolType),
                TuiMarkup.ColorStatus(tool.Status),
                Markup.Escape(tool.Endpoint ?? "—"));
        }

        AnsiConsole.Write(table);
    }

    public static void RenderManifestTools(string workspaceName, IEnumerable<KeyValuePair<string, string>> tools)
    {
        var table = CliTheme.CreateTable($"Tools · {workspaceName}");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Type"));

        foreach (var (toolName, toolType) in tools.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            table.AddRow($"[bold white]{Markup.Escape(toolName)}[/]", Markup.Escape(toolType));

        AnsiConsole.Write(table);
    }
}
