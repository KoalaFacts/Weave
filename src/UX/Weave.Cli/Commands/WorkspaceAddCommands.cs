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

internal static class WorkspaceAddToolCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Tool name" };
        var typeOption = new Option<string>("--type")
        {
            Description = "Tool type (mcp, cli, openapi)",
            DefaultValueFactory = _ => "mcp"
        };
        typeOption.CompletionSources.Add(CliCompletions.CompleteToolTypes);

        var cmd = new Command("tool", "Add a tool") { workspaceArg, nameOption, typeOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var toolName = parseResult.GetValue(nameOption);
            var type = parseResult.GetValue(typeOption)!;

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(toolName))
            {
                toolName = AnsiConsole.Prompt(new TextPrompt<string>("Tool name:").Styled());
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Tools.ContainsKey(toolName))
            {
                CliTheme.WriteWarning($"Tool '{toolName}' already exists in the workspace.");
                return 1;
            }

            manifest.Tools[toolName] = new ToolDefinition { Type = type };

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            CliTheme.WriteSuccess($"Tool '{toolName}' added to workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }
}

internal static class WorkspaceAddTargetCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Target name" };
        var runtimeOption = new Option<string>("--runtime")
        {
            Description = "Runtime type (podman, docker)",
            DefaultValueFactory = _ => "podman"
        };
        runtimeOption.CompletionSources.Add(CliCompletions.CompleteRuntimeTypes);

        var cmd = new Command("target", "Add a deployment target") { workspaceArg, nameOption, runtimeOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var targetName = parseResult.GetValue(nameOption);
            var runtime = parseResult.GetValue(runtimeOption)!;

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(targetName))
            {
                targetName = AnsiConsole.Prompt(new TextPrompt<string>("Target name:").Styled());
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Targets.ContainsKey(targetName))
            {
                CliTheme.WriteWarning($"Target '{targetName}' already exists in the workspace.");
                return 1;
            }

            manifest.Targets[targetName] = new TargetDefinition { Runtime = runtime };

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            CliTheme.WriteSuccess($"Target '{targetName}' added to workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }
}
