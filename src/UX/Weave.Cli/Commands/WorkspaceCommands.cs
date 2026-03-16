using System.CommandLine;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceNewCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        var presetOption = new Option<string?>("--preset") { Description = "Use a built-in preset (starter, coding-assistant, research, multi-agent)" };
        presetOption.CompletionSources.Add(CliCompletions.CompletePresetNames);

        var cmd = new Command("new", "Create a new workspace") { nameArg, presetOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var preset = parseResult.GetValue(presetOption);

            string model;
            List<string> tools;
            string? selectedPresetName = preset;
            var isolation = IsolationLevel.Full;

            if (preset is not null)
            {
                if (!WorkspacePresets.All.TryGetValue(preset, out var presetDef))
                    {
                        CliTheme.WriteError($"Unknown preset '{preset}'. Use 'weave workspace presets' to see available options.");
                        return 1;
                    }

                model = presetDef.Model;
                tools = [.. presetDef.Tools];
            }
            else
            {
                var presetChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Choose a preset:")
                        .Styled()
                        .AddChoices([.. WorkspacePresets.All.Keys, "custom (configure everything yourself)"]));

                if (presetChoice != "custom (configure everything yourself)" &&
                    WorkspacePresets.All.TryGetValue(presetChoice, out var selectedPreset))
                {
                    selectedPresetName = presetChoice;
                    model = selectedPreset.Model;
                    tools = [.. selectedPreset.Tools];
                }
                else
                {
                    model = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select a model for your assistant:")
                            .Styled()
                            .AddChoices("claude-sonnet-4-20250514", "gpt-4o", "custom..."));

                    if (model == "custom...")
                    {
                        model = AnsiConsole.Prompt(
                            new TextPrompt<string>("Enter the model name:").Styled());
                    }

                    tools = AnsiConsole.Prompt(
                        new MultiSelectionPrompt<string>()
                            .Title("Which tools should the assistant have access to?")
                            .Styled()
                            .NotRequired()
                            .AddChoices("git", "file", "web", "document"));

                    var isolationChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Workspace isolation level:")
                            .Styled()
                            .AddChoices("full (recommended)", "shared", "none"));

                    isolation = isolationChoice switch
                    {
                        "shared" => IsolationLevel.Shared,
                        "none" => IsolationLevel.None,
                        _ => IsolationLevel.Full
                    };
                }
            }

            var basePath = Path.Combine("workspaces", name);
            Directory.CreateDirectory(basePath);
            Directory.CreateDirectory(Path.Combine(basePath, "prompts"));
            Directory.CreateDirectory(Path.Combine(basePath, "data"));
            Directory.CreateDirectory(Path.Combine(basePath, ".weave"));

            var isMultiAgent = string.Equals(selectedPresetName, "multi-agent", StringComparison.OrdinalIgnoreCase);

            Dictionary<string, AgentDefinition> agents;
            List<(string FileName, string Content)> promptFiles;

            if (isMultiAgent)
            {
                agents = new Dictionary<string, AgentDefinition>
                {
                    ["supervisor"] = new AgentDefinition
                    {
                        Model = model,
                        SystemPromptFile = "./prompts/supervisor.md",
                        MaxConcurrentTasks = 5,
                        Tools = tools
                    },
                    ["worker"] = new AgentDefinition
                    {
                        Model = model,
                        SystemPromptFile = "./prompts/worker.md",
                        MaxConcurrentTasks = 3,
                        Tools = tools
                    }
                };

                promptFiles =
                [
                    ("supervisor.md", "# Supervisor\n\nYou coordinate tasks across worker assistants. Break complex requests into subtasks and delegate them.\n"),
                    ("worker.md", "# Worker\n\nYou execute tasks assigned by the supervisor. Focus on completing one task at a time with high quality.\n")
                ];
            }
            else
            {
                agents = new Dictionary<string, AgentDefinition>
                {
                    ["assistant"] = new AgentDefinition
                    {
                        Model = model,
                        SystemPromptFile = "./prompts/assistant.md",
                        MaxConcurrentTasks = 3,
                        Tools = tools
                    }
                };

                promptFiles =
                [
                    ("assistant.md", "# Assistant\n\nYou are a helpful AI assistant.\n")
                ];
            }

            var manifest = new WorkspaceManifest
            {
                Version = "1.0",
                Name = name,
                Workspace = new WorkspaceConfig
                {
                    Isolation = isolation,
                    Network = new NetworkConfig { Name = $"weave-{name}" },
                    Secrets = new SecretsConfig { Provider = "env" }
                },
                Agents = agents,
                Tools = tools.ToDictionary(t => t, _ => new ToolDefinition { Type = "mcp" }),
                Targets = new Dictionary<string, TargetDefinition>
                {
                    ["local"] = new TargetDefinition { Runtime = "podman" }
                }
            };

            var parser = new ManifestParser();
            await File.WriteAllTextAsync(Path.Combine(basePath, "workspace.json"), parser.Serialize(manifest), cancellationToken);

            foreach (var (fileName, content) in promptFiles)
            {
                await File.WriteAllTextAsync(Path.Combine(basePath, "prompts", fileName), content, cancellationToken);
            }

            CliTheme.WriteSuccess($"Workspace \"{name}\" created.");
            CliTheme.WriteMuted($"  Run `weave workspace up {name}` to start.");
            return 0;
        });

        return cmd;
    }
}

internal static class WorkspaceListCommand
{
    public static Command Create()
    {
        var cmd = new Command("list", "List all workspaces");
        cmd.SetAction(parseResult =>
        {
            var workspacesDir = "workspaces";
            if (!Directory.Exists(workspacesDir))
            {
                CliTheme.WriteWarning("No workspaces found.");
                return 0;
            }

            var dirs = Directory.GetDirectories(workspacesDir);
            if (dirs.Length == 0)
            {
                CliTheme.WriteWarning("No workspaces found.");
                return 0;
            }

            var table = CliTheme.CreateTable("Workspaces");
            table.AddColumn(CliTheme.StyledColumn("Name"));
            table.AddColumn(CliTheme.StyledColumn("Path"));
            table.AddColumn(CliTheme.StyledColumn("Status"));

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                var hasManifest = File.Exists(Path.Combine(dir, "workspace.json"));
                var status = hasManifest
                    ? $"[rgb({CliTheme.Success.R},{CliTheme.Success.G},{CliTheme.Success.B})]Ready[/]"
                    : $"[rgb({CliTheme.Error.R},{CliTheme.Error.G},{CliTheme.Error.B})]Invalid[/]";
                table.AddRow(name, dir, status);
            }

            AnsiConsole.Write(table);
            return 0;
        });

        return cmd;
    }
}

internal static class WorkspaceRemoveCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var purgeOption = new Option<bool>("--purge") { Description = "Delete workspace folder" };

        var cmd = new Command("remove", "Remove a workspace") { nameArg, purgeOption };
        cmd.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var purge = parseResult.GetValue(purgeOption);

            var path = Path.Combine("workspaces", name);
            if (!Directory.Exists(path))
            {
                CliTheme.WriteError($"Workspace '{name}' not found.");
                return 1;
            }

            if (purge)
            {
                Directory.Delete(path, recursive: true);
                CliTheme.WriteSuccess($"Workspace '{name}' purged.");
            }
            else
            {
                CliTheme.WriteInfo($"Workspace '{name}' deregistered.");
                CliTheme.WriteMuted("  Use --purge to delete files.");
            }

            return 0;
        });

        return cmd;
    }
}
