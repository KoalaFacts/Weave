using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

public sealed class WorkspaceAddAgentCommand : AsyncCommand<WorkspaceAddAgentCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; init; } = string.Empty;

        [CommandOption("--name <NAME>")]
        [Description("Agent name")]
        public string? Name { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description("Model to use")]
        [DefaultValue("claude-sonnet-4-20250514")]
        public string Model { get; init; } = "claude-sonnet-4-20250514";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.json found for '{settings.Workspace}'.[/]");
            return 1;
        }

        var agentName = settings.Name;
        if (string.IsNullOrWhiteSpace(agentName))
        {
            agentName = AnsiConsole.Prompt(new TextPrompt<string>("Agent name:"));
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = parser.Parse(json);

        if (manifest.Agents.ContainsKey(agentName))
        {
            AnsiConsole.MarkupLine($"[yellow]Agent '{agentName}' already exists in the workspace.[/]");
            return 1;
        }

        manifest.Agents[agentName] = new AgentDefinition
        {
            Model = settings.Model,
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

        AnsiConsole.MarkupLine($"[green]Agent '{agentName}' added to workspace '{settings.Workspace}'.[/]");
        return 0;
    }
}

public sealed class WorkspaceAddToolCommand : AsyncCommand<WorkspaceAddToolCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; init; } = string.Empty;

        [CommandOption("--name <NAME>")]
        [Description("Tool name")]
        public string? Name { get; init; }

        [CommandOption("--type <TYPE>")]
        [Description("Tool type (mcp, cli, openapi)")]
        [DefaultValue("mcp")]
        public string Type { get; init; } = "mcp";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.json found for '{settings.Workspace}'.[/]");
            return 1;
        }

        var toolName = settings.Name;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            toolName = AnsiConsole.Prompt(new TextPrompt<string>("Tool name:"));
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = parser.Parse(json);

        if (manifest.Tools.ContainsKey(toolName))
        {
            AnsiConsole.MarkupLine($"[yellow]Tool '{toolName}' already exists in the workspace.[/]");
            return 1;
        }

        manifest.Tools[toolName] = new ToolDefinition { Type = settings.Type };

        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

        AnsiConsole.MarkupLine($"[green]Tool '{toolName}' added to workspace '{settings.Workspace}'.[/]");
        return 0;
    }
}

public sealed class WorkspaceAddTargetCommand : AsyncCommand<WorkspaceAddTargetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; init; } = string.Empty;

        [CommandOption("--name <NAME>")]
        [Description("Target name")]
        public string? Name { get; init; }

        [CommandOption("--runtime <RUNTIME>")]
        [Description("Runtime type (podman, docker)")]
        [DefaultValue("podman")]
        public string Runtime { get; init; } = "podman";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.json found for '{settings.Workspace}'.[/]");
            return 1;
        }

        var targetName = settings.Name;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = AnsiConsole.Prompt(new TextPrompt<string>("Target name:"));
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = parser.Parse(json);

        if (manifest.Targets.ContainsKey(targetName))
        {
            AnsiConsole.MarkupLine($"[yellow]Target '{targetName}' already exists in the workspace.[/]");
            return 1;
        }

        manifest.Targets[targetName] = new TargetDefinition { Runtime = settings.Runtime };

        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

        AnsiConsole.MarkupLine($"[green]Target '{targetName}' added to workspace '{settings.Workspace}'.[/]");
        return 0;
    }
}
