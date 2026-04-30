using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Manifest;
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
        var manifestPath = ManifestResolver.Resolve(options.Workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Workspace}'.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(agentName))
            agentName = AnsiConsole.Prompt(new TextPrompt<string>("Agent name:").Styled());

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = parser.Parse(json);

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

        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), ct);

        var promptPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "prompts", $"{agentName}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        if (!File.Exists(promptPath))
        {
            await File.WriteAllTextAsync(
                promptPath,
                string.Create(CultureInfo.InvariantCulture, $"# {agentName}\n\nYou are a helpful AI assistant.\n"),
                ct);
        }

        CliTheme.WriteSuccess($"Agent '{agentName}' added to workspace '{options.Workspace}'.");
        return 0;
    }
}
