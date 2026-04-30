using Spectre.Console;
using Weave.Workspaces.Manifest;
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
        var manifestPath = ManifestResolver.Resolve(options.Workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Workspace}'.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(toolName))
            toolName = AnsiConsole.Prompt(new TextPrompt<string>("Tool name:").Styled());

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = parser.Parse(json);

        if (manifest.Tools.ContainsKey(toolName))
        {
            CliTheme.WriteWarning($"Tool '{toolName}' already exists in the workspace.");
            return 1;
        }

        manifest.Tools[toolName] = new ToolDefinition { Type = options.Type };
        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), ct);

        CliTheme.WriteSuccess($"Tool '{toolName}' added to workspace '{options.Workspace}'.");
        return 0;
    }
}
