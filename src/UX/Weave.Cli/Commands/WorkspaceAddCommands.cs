using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.RepresentationModel;

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
            AnsiConsole.MarkupLine($"[red]No workspace.yml found for '{settings.Workspace}'.[/]");
            return 1;
        }

        var agentName = settings.Name;
        if (string.IsNullOrWhiteSpace(agentName))
        {
            agentName = AnsiConsole.Prompt(new TextPrompt<string>("Agent name:"));
        }

        var yaml = new YamlStream();
        using (var reader = new StreamReader(manifestPath))
        {
            yaml.Load(reader);
        }

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var agentsKey = new YamlScalarNode("agents");

        if (!root.Children.TryGetValue(agentsKey, out var agentsNode) || agentsNode is not YamlMappingNode agentsMapping)
        {
            agentsMapping = new YamlMappingNode();
            root.Children[agentsKey] = agentsMapping;
        }

        var agentNameNode = new YamlScalarNode(agentName);
        if (agentsMapping.Children.ContainsKey(agentNameNode))
        {
            AnsiConsole.MarkupLine($"[yellow]Agent '{agentName}' already exists in the workspace.[/]");
            return 1;
        }

        var agentDef = new YamlMappingNode
        {
            { "model", settings.Model },
            { "system_prompt_file", $"./prompts/{agentName}.md" },
            { "max_concurrent_tasks", "3" },
            { "tools", new YamlSequenceNode() }
        };
        agentsMapping.Children[agentNameNode] = agentDef;

        using (var writer = new StreamWriter(manifestPath))
        {
            yaml.Save(writer, assignAnchors: false);
        }

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

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.yml found for '{settings.Workspace}'.[/]");
            return Task.FromResult(1);
        }

        var toolName = settings.Name;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            toolName = AnsiConsole.Prompt(new TextPrompt<string>("Tool name:"));
        }

        var yaml = new YamlStream();
        using (var reader = new StreamReader(manifestPath))
        {
            yaml.Load(reader);
        }

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var toolsKey = new YamlScalarNode("tools");

        if (!root.Children.TryGetValue(toolsKey, out var toolsNode) || toolsNode is not YamlMappingNode toolsMapping)
        {
            toolsMapping = new YamlMappingNode();
            root.Children[toolsKey] = toolsMapping;
        }

        var toolNameNode = new YamlScalarNode(toolName);
        if (toolsMapping.Children.ContainsKey(toolNameNode))
        {
            AnsiConsole.MarkupLine($"[yellow]Tool '{toolName}' already exists in the workspace.[/]");
            return Task.FromResult(1);
        }

        var toolDef = new YamlMappingNode
        {
            { "type", settings.Type }
        };
        toolsMapping.Children[toolNameNode] = toolDef;

        using (var writer = new StreamWriter(manifestPath))
        {
            yaml.Save(writer, assignAnchors: false);
        }

        AnsiConsole.MarkupLine($"[green]Tool '{toolName}' added to workspace '{settings.Workspace}'.[/]");
        return Task.FromResult(0);
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

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.yml found for '{settings.Workspace}'.[/]");
            return Task.FromResult(1);
        }

        var targetName = settings.Name;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = AnsiConsole.Prompt(new TextPrompt<string>("Target name:"));
        }

        var yaml = new YamlStream();
        using (var reader = new StreamReader(manifestPath))
        {
            yaml.Load(reader);
        }

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var targetsKey = new YamlScalarNode("targets");

        if (!root.Children.TryGetValue(targetsKey, out var targetsNode) || targetsNode is not YamlMappingNode targetsMapping)
        {
            targetsMapping = new YamlMappingNode();
            root.Children[targetsKey] = targetsMapping;
        }

        var targetNameNode = new YamlScalarNode(targetName);
        if (targetsMapping.Children.ContainsKey(targetNameNode))
        {
            AnsiConsole.MarkupLine($"[yellow]Target '{targetName}' already exists in the workspace.[/]");
            return Task.FromResult(1);
        }

        var targetDef = new YamlMappingNode
        {
            { "runtime", settings.Runtime }
        };
        targetsMapping.Children[targetNameNode] = targetDef;

        using (var writer = new StreamWriter(manifestPath))
        {
            yaml.Save(writer, assignAnchors: false);
        }

        AnsiConsole.MarkupLine($"[green]Target '{targetName}' added to workspace '{settings.Workspace}'.[/]");
        return Task.FromResult(0);
    }
}
