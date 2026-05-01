using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddTargetCliCommand(WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspaceAddTargetOptions>
{
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "target";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Add a deployment target";

    public async Task<int> ExecuteAsync(WorkspaceAddTargetOptions options, CancellationToken ct)
    {
        var targetName = options.TargetName;
        var manifestPath = ManifestResolver.Resolve(options.Workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Workspace}'.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(targetName))
            targetName = AnsiConsole.Prompt(new TextPrompt<string>("Target name:").Styled());

        var manifest = await _manifests.ReadAsync(manifestPath, ct);

        if (manifest.Targets.ContainsKey(targetName))
        {
            CliTheme.WriteWarning($"Target '{targetName}' already exists in the workspace.");
            return 1;
        }

        manifest.Targets[targetName] = new TargetDefinition { Runtime = options.Runtime };
        await _manifests.WriteAsync(manifestPath, manifest, ct);

        CliTheme.WriteSuccess($"Target '{targetName}' added to workspace '{options.Workspace}'.");
        return 0;
    }
}
