using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePluginRemoveCliCommand(WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspacePluginRemoveOptions>
{
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "remove";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Remove a plugin";

    public async Task<int> ExecuteAsync(WorkspacePluginRemoveOptions options, CancellationToken ct)
    {
        var workspace = WorkspacePrompt.SelectName(options.Workspace, "Which workspace would you like to update?");
        var manifestPath = ManifestResolver.Resolve(workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError(workspace is null
                ? "No workspace.json found. Create one first with: weave workspace new"
                : $"No workspace.json found for '{workspace}'.");
            return 1;
        }

        var manifest = await _manifests.ReadAsync(manifestPath, ct);

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

        await _manifests.WriteAsync(manifestPath, manifest, ct);

        CliTheme.WriteSuccess($"Plugin '{pluginName}' removed from workspace '{manifest.Name}'.");
        return 0;
    }
}
