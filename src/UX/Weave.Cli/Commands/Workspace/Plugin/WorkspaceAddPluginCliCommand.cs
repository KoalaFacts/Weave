using Weave.Workspaces.Manifest;
namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddPluginCliCommand(
    IManifestResolver manifestResolver,
    WorkspacePrompt workspacePrompt,
    WorkspacePluginPrompt? prompt = null) : ICliCommand<WorkspaceAddPluginOptions>
{
    private readonly WorkspacePluginPrompt _prompt = prompt ?? new WorkspacePluginPrompt();

    public string Name => "plugin";

    public IReadOnlyList<string> Aliases => ["add"];

    public string Description => "Add a plugin";

    public async Task<int> ExecuteAsync(WorkspaceAddPluginOptions options, CancellationToken ct)
    {
        var workspace = workspacePrompt.SelectName(options.Workspace, "Which workspace would you like to update?");
        var manifestPath = manifestResolver.Resolve(workspace);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspace);
            return 1;
        }

        var type = _prompt.SelectType(options.Type);
        var pluginName = WorkspacePluginPrompt.SelectName(options.PluginName, type);

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);

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

        await WorkspaceManifestFile.WriteAsync(manifestPath, manifest, ct);

        CliTheme.WriteSuccess($"Plugin '{pluginName}' ({type}) added to workspace '{manifest.Name}'.");
        return 0;
    }
}
