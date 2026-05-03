using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddAgentCliCommand : ICliCommand<WorkspaceAddAgentOptions>
{

    public string Name => "agent";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Add an assistant";

    public async Task<int> ExecuteAsync(WorkspaceAddAgentOptions options, CancellationToken ct)
    {
        var agentName = options.AgentName;
        var workspace = WorkspacePrompt.SelectName(options.Workspace, "Which workspace would you like to update?");
        var manifestPath = ManifestResolver.Resolve(workspace);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspace);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(agentName))
            agentName = AnsiConsole.Prompt(new TextPrompt<string>("Agent name:").Styled());

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);

        if (manifest.Agents.ContainsKey(agentName))
        {
            CliTheme.WriteWarning($"Agent '{agentName}' already exists in the workspace.");
            return 1;
        }

        manifest.Agents[agentName] = new AgentDefinition
        {
            Model = options.Model,
            SystemPromptFile = $"./prompts/{agentName}.md",
            MaxConcurrentTasks = 3
        };

        await WorkspaceManifestFile.WriteAsync(manifestPath, manifest, ct);

        var promptPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "prompts", $"{agentName}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        if (!File.Exists(promptPath))
        {
            await File.WriteAllTextAsync(
                promptPath,
                string.Create(CultureInfo.InvariantCulture, $"# {agentName}\n\nYou are a helpful AI assistant.\n"),
                ct);
        }

        CliTheme.WriteSuccess($"Agent '{agentName}' added to workspace '{manifest.Name}'.");
        return 0;
    }
}
