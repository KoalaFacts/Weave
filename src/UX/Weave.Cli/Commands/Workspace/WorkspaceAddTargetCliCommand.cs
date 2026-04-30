using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddTargetCliCommand : ICliCommand<WorkspaceAddTargetOptions>
{
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

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = parser.Parse(json);

        if (manifest.Targets.ContainsKey(targetName))
        {
            CliTheme.WriteWarning($"Target '{targetName}' already exists in the workspace.");
            return 1;
        }

        manifest.Targets[targetName] = new TargetDefinition { Runtime = options.Runtime };
        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), ct);

        CliTheme.WriteSuccess($"Target '{targetName}' added to workspace '{options.Workspace}'.");
        return 0;
    }
}
