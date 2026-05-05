using Spectre.Console;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
namespace Weave.Cli.Tui;

internal static class TuiManifestView
{
    public static void Render(WorkspaceManifest manifest, string manifestPath)
    {
        var table = CliTheme.CreateTable("Manifest");
        table.AddColumn(CliTheme.StyledColumn("Property"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Workspace", $"[bold white]{Markup.Escape(manifest.Name)}[/]");
        table.AddRow("Version", Markup.Escape(manifest.Version));
        table.AddRow("Isolation", manifest.Workspace.Isolation.ToString());
        table.AddRow("Manifest", Markup.Escape(manifestPath));
        AnsiConsole.Write(table);

        if (manifest.Agents is { Count: > 0 })
            RenderAgents(manifest);

        if (manifest.Tools is { Count: > 0 })
            RenderTools(manifest);
    }

    private static void RenderAgents(WorkspaceManifest manifest)
    {
        var agentTable = CliTheme.CreateTable("Agents (from manifest)");
        agentTable.AddColumn(CliTheme.StyledColumn("Name"));
        agentTable.AddColumn(CliTheme.StyledColumn("Model"));
        agentTable.AddColumn(CliTheme.StyledColumn("Tools"));

        foreach (var (agentName, agent) in manifest.Agents)
        {
            agentTable.AddRow(
                $"[bold white]{Markup.Escape(agentName)}[/]",
                Markup.Escape(agent.Model),
                Markup.Escape(string.Join(", ", agent.Tools)));
        }

        AnsiConsole.Write(agentTable);
    }

    private static void RenderTools(WorkspaceManifest manifest)
    {
        var toolTable = CliTheme.CreateTable("Tools (from manifest)");
        toolTable.AddColumn(CliTheme.StyledColumn("Name"));
        toolTable.AddColumn(CliTheme.StyledColumn("Type"));

        foreach (var (toolName, tool) in manifest.Tools)
            toolTable.AddRow(
                $"[bold white]{Markup.Escape(toolName)}[/]",
                Markup.Escape(tool.Type));

        AnsiConsole.Write(toolTable);
    }
}
