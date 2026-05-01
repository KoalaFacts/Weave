using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePluginListCliCommand : ICliCommand<WorkspaceNameOptions>
{
    public string Name => "list";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "List configured plugins";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var manifestPath = ManifestResolver.Resolve(options.Name);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Name}'.");
            return 1;
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, ct);
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
            table.AddRow(name, plugin.Type, plugin.Description ?? "[dim]—[/]", configStr);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
