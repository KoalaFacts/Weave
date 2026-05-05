using Spectre.Console;
using Weave.Workspaces.Manifest;
namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddTargetCliCommand : ICliCommand<WorkspaceAddTargetOptions>
{

    public string Name => "target";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Add a deployment target";

    public async Task<int> ExecuteAsync(WorkspaceAddTargetOptions options, CancellationToken ct)
    {
        var targetName = options.TargetName;
        var workspace = WorkspacePrompt.SelectName(options.Workspace, "Which workspace would you like to update?");
        var manifestPath = ManifestResolver.Resolve(workspace);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspace);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(targetName))
            targetName = AnsiConsole.Prompt(new TextPrompt<string>("Target name:").Styled());

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);

        if (manifest.Targets.ContainsKey(targetName))
        {
            CliTheme.WriteWarning($"Target '{targetName}' already exists in the workspace.");
            return 1;
        }

        manifest.Targets[targetName] = new TargetDefinition { Runtime = options.Runtime };
        await WorkspaceManifestFile.WriteAsync(manifestPath, manifest, ct);

        CliTheme.WriteSuccess($"Target '{targetName}' added to workspace '{manifest.Name}'.");
        return 0;
    }
}
