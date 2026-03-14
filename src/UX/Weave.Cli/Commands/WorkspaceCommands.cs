using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

public sealed class WorkspaceNewCommand : AsyncCommand<WorkspaceNewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--preset <PRESET>")]
        [Description("Use a built-in preset (starter, coding-assistant, research, multi-agent)")]
        public string? Preset { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var name = settings.Name;
        var preset = settings.Preset;

        string model;
        List<string> tools;

        if (preset is not null)
        {
            if (!WorkspacePresets.All.TryGetValue(preset, out var presetDef))
            {
                AnsiConsole.MarkupLine($"[red]Unknown preset '{preset}'. Use 'weave workspace presets' to see available options.[/]");
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
                    .AddChoices([.. WorkspacePresets.All.Keys, "custom (configure everything yourself)"]));

            if (presetChoice != "custom (configure everything yourself)" &&
                WorkspacePresets.All.TryGetValue(presetChoice, out var selectedPreset))
            {
                model = selectedPreset.Model;
                tools = [.. selectedPreset.Tools];
            }
            else
            {
                model = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select a model for your assistant:")
                        .AddChoices("claude-sonnet-4-20250514", "gpt-4o", "custom..."));

                if (model == "custom...")
                {
                    model = AnsiConsole.Prompt(
                        new TextPrompt<string>("Enter the model name:"));
                }

                tools = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<string>()
                        .Title("Which tools should the assistant have access to?")
                        .NotRequired()
                        .AddChoices("git", "file", "web", "document"));
            }
        }

        var basePath = Path.Combine("workspaces", name);
        Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(Path.Combine(basePath, "prompts"));
        Directory.CreateDirectory(Path.Combine(basePath, "data"));
        Directory.CreateDirectory(Path.Combine(basePath, ".weave"));

        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = name,
            Workspace = new WorkspaceConfig
            {
                Isolation = IsolationLevel.Full,
                Network = new NetworkConfig { Name = $"weave-{name}" },
                Secrets = new SecretsConfig { Provider = "env" }
            },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new AgentDefinition
                {
                    Model = model,
                    SystemPromptFile = "./prompts/assistant.md",
                    MaxConcurrentTasks = 3,
                    Tools = tools
                }
            },
            Tools = tools.ToDictionary(t => t, _ => new ToolDefinition { Type = "mcp" }),
            Targets = new Dictionary<string, TargetDefinition>
            {
                ["local"] = new TargetDefinition { Runtime = "podman" }
            }
        };

        var parser = new ManifestParser();
        await File.WriteAllTextAsync(Path.Combine(basePath, "workspace.json"), parser.Serialize(manifest), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(basePath, "prompts", "assistant.md"),
            "# Assistant\n\nYou are a helpful AI assistant.\n", cancellationToken);

        AnsiConsole.MarkupLine($"[green]✔ Workspace \"{name}\" created.[/] Run `weave workspace up {name}` to start.");
        return 0;
    }
}

public sealed class WorkspaceListCommand : Command<WorkspaceListCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var workspacesDir = "workspaces";
        if (!Directory.Exists(workspacesDir))
        {
            AnsiConsole.MarkupLine("[yellow]No workspaces found.[/]");
            return 0;
        }

        var dirs = Directory.GetDirectories(workspacesDir);
        if (dirs.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No workspaces found.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("Path");
        table.AddColumn("Status");

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            var hasManifest = File.Exists(Path.Combine(dir, "workspace.json"));
            var status = hasManifest ? "[green]Ready[/]" : "[red]Invalid[/]";
            table.AddRow(name, dir, status);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class WorkspaceRemoveCommand : Command<WorkspaceRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--purge")]
        [Description("Delete workspace folder")]
        public bool Purge { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var path = Path.Combine("workspaces", settings.Name);
        if (!Directory.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Workspace '{settings.Name}' not found.[/]");
            return 1;
        }

        if (settings.Purge)
        {
            Directory.Delete(path, recursive: true);
            AnsiConsole.MarkupLine($"[green]Workspace '{settings.Name}' purged.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"Workspace '{settings.Name}' deregistered. Use [bold]--purge[/] to delete files.");
        }

        return 0;
    }
}
