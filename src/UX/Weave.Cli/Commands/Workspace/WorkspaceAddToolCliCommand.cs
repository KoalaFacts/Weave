using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddToolCliCommand : ICliCommand<WorkspaceAddToolOptions>
{

    public string Name => "tool";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Add a tool";

    public async Task<int> ExecuteAsync(WorkspaceAddToolOptions options, CancellationToken ct)
    {
        var toolName = options.ToolName;
        var workspace = WorkspacePrompt.SelectName(options.Workspace, "Which workspace would you like to update?");
        var manifestPath = ManifestResolver.Resolve(workspace);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspace);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(toolName))
            toolName = AnsiConsole.Prompt(new TextPrompt<string>("Tool name:").Styled());

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);

        if (manifest.Tools.ContainsKey(toolName))
        {
            CliTheme.WriteWarning($"Tool '{toolName}' already exists in the workspace.");
            return 1;
        }

        manifest.Tools[toolName] = new ToolDefinition { Type = options.Type };
        await WorkspaceManifestFile.WriteAsync(manifestPath, manifest, ct);

        CliTheme.WriteSuccess($"Tool '{toolName}' added to workspace '{manifest.Name}'.");
        return 0;
    }
}
