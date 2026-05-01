using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddPluginCliCommand(
    WorkspacePluginPrompt? prompt = null,
    WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspaceAddPluginOptions>
{
    private readonly WorkspacePluginPrompt _prompt = prompt ?? new WorkspacePluginPrompt();
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "plugin";

    public IReadOnlyList<string> Aliases => ["add"];

    public string Description => "Add a plugin";

    public async Task<int> ExecuteAsync(WorkspaceAddPluginOptions options, CancellationToken ct)
    {
        var manifestPath = ManifestResolver.Resolve(options.Workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Workspace}'.");
            return 1;
        }

        var type = _prompt.SelectType(options.Type);
        var pluginName = _prompt.SelectName(options.PluginName, type);

        var manifest = await _manifests.ReadAsync(manifestPath, ct);

        if (manifest.Plugins.ContainsKey(pluginName))
        {
            CliTheme.WriteWarning($"Plugin '{pluginName}' already exists in the workspace.");
            return 1;
        }

        var configuration = _prompt.Configure(type);

        manifest.Plugins[pluginName] = new PluginDefinition
        {
            Type = type,
            Description = configuration.Description,
            Config = configuration.Config
        };

        await _manifests.WriteAsync(manifestPath, manifest, ct);

        CliTheme.WriteSuccess($"Plugin '{pluginName}' ({type}) added to workspace '{options.Workspace}'.");
        return 0;
    }
}
