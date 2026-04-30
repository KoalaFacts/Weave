using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceAddAgentCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Agent name" };
        var modelOption = new Option<string>("--model")
        {
            Description = "Model to use",
            DefaultValueFactory = _ => "claude-sonnet-4-20250514"
        };

        var cmd = new Command("agent", "Add an assistant") { workspaceArg, nameOption, modelOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var agentName = parseResult.GetValue(nameOption);
            var model = parseResult.GetValue(modelOption)!;

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(agentName))
            {
                agentName = AnsiConsole.Prompt(new TextPrompt<string>("Agent name:").Styled());
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Agents.ContainsKey(agentName))
            {
                CliTheme.WriteWarning($"Agent '{agentName}' already exists in the workspace.");
                return 1;
            }

            manifest.Agents[agentName] = new AgentDefinition
            {
                Model = model,
                SystemPromptFile = $"./prompts/{agentName}.md",
                MaxConcurrentTasks = 3
            };

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            var promptPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "prompts", $"{agentName}.md");
            Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
            if (!File.Exists(promptPath))
            {
                await File.WriteAllTextAsync(promptPath,
                    string.Create(CultureInfo.InvariantCulture, $"# {agentName}\n\nYou are a helpful AI assistant.\n"), cancellationToken);
            }

            CliTheme.WriteSuccess($"Agent '{agentName}' added to workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }
}
