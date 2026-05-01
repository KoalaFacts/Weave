using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePluginRemoveCliCommand : ICliCommand<WorkspacePluginRemoveOptions>
{
    public string Name => "remove";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Remove a plugin";

    public async Task<int> ExecuteAsync(WorkspacePluginRemoveOptions options, CancellationToken ct)
    {
        var manifestPath = ManifestResolver.Resolve(options.Workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Workspace}'.");
            return 1;
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = parser.Parse(json);

        if (manifest.Plugins.Count == 0)
        {
            CliTheme.WriteWarning("No plugins configured in this workspace.");
            return 0;
        }

        var pluginName = options.PluginName;
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            pluginName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select plugin to remove:")
                    .Styled()
                    .AddChoices(manifest.Plugins.Keys));
        }

        if (!manifest.Plugins.Remove(pluginName))
        {
            CliTheme.WriteWarning($"Plugin '{pluginName}' not found in the workspace.");
            return 1;
        }

        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), ct);

        CliTheme.WriteSuccess($"Plugin '{pluginName}' removed from workspace '{options.Workspace}'.");
        return 0;
    }
}
