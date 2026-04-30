using System.CommandLine;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class WorkspacePluginListCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("list", "List configured plugins") { workspaceArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Plugins.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No plugins configured in this workspace.[/]");
                return 0;
            }

            var table = new Table();
            table.AddColumn("Name");
            table.AddColumn("Type");
            table.AddColumn("Description");
            table.AddColumn("Config");

            foreach (var (name, plugin) in manifest.Plugins)
            {
                var configStr = plugin.Config.Count > 0
                    ? string.Join(", ", plugin.Config.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "[dim]—[/]";
                table.AddRow(
                    name,
                    plugin.Type,
                    plugin.Description ?? "[dim]—[/]",
                    configStr);
            }

            AnsiConsole.Write(table);
            return 0;
        });

        return cmd;
    }
}
