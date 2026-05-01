using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePluginListCliCommand(WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspaceNameOptions>
{
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "list";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "List configured plugins";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var workspaceName = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to inspect?");
        var manifestPath = ManifestResolver.Resolve(workspaceName);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspaceName);
            return 1;
        }

        var manifest = await _manifests.ReadAsync(manifestPath, ct);

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
