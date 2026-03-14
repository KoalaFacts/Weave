using System.Collections.Frozen;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

public sealed class WorkspaceAddPluginCommand : AsyncCommand<WorkspaceAddPluginCommand.Settings>
{
    private static readonly FrozenDictionary<string, PluginTemplate> Templates =
        new Dictionary<string, PluginTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["dapr"] = new("dapr", "Dapr sidecar for pub/sub events and service invocation",
                new Dictionary<string, string> { ["port"] = "3500" }),
            ["vault"] = new("vault", "HashiCorp Vault for production secret management",
                new Dictionary<string, string> { ["address"] = "http://localhost:8200" }),
            ["http"] = new("http", "Generic HTTP/REST endpoint",
                new Dictionary<string, string> { ["base_url"] = "http://localhost:8080" }),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; init; } = string.Empty;

        [CommandOption("--name <NAME>")]
        [Description("Plugin name")]
        public string? Name { get; init; }

        [CommandOption("--type <TYPE>")]
        [Description("Plugin type (dapr, vault, http, custom)")]
        public string? Type { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.json found for '{settings.Workspace}'.[/]");
            return 1;
        }

        // Pick type interactively or from --type
        var type = settings.Type;
        if (string.IsNullOrWhiteSpace(type))
        {
            type = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select plugin type:")
                    .AddChoices("dapr", "vault", "http", "custom"));
        }

        // Pick name interactively or from --name
        var pluginName = settings.Name;
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            pluginName = AnsiConsole.Prompt(
                new TextPrompt<string>("Plugin name:")
                    .DefaultValue(type));
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = parser.Parse(json);

        if (manifest.Plugins.ContainsKey(pluginName))
        {
            AnsiConsole.MarkupLine($"[yellow]Plugin '{pluginName}' already exists in the workspace.[/]");
            return 1;
        }

        // Build config — use template defaults or prompt for custom
        Dictionary<string, string> config;
        string? description;

        if (Templates.TryGetValue(type, out var template))
        {
            description = template.Description;
            config = new Dictionary<string, string>(template.DefaultConfig);

            // Let the user confirm or override each config value
            foreach (var key in template.DefaultConfig.Keys.ToArray())
            {
                config[key] = AnsiConsole.Prompt(
                    new TextPrompt<string>($"  {key}:")
                        .DefaultValue(template.DefaultConfig[key]));
            }
        }
        else
        {
            description = AnsiConsole.Prompt(
                new TextPrompt<string>("Description (optional):")
                    .AllowEmpty());
            if (string.IsNullOrWhiteSpace(description)) description = null;

            config = [];
            while (AnsiConsole.Confirm("Add a config value?", false))
            {
                var key = AnsiConsole.Prompt(new TextPrompt<string>("  Key:"));
                var value = AnsiConsole.Prompt(new TextPrompt<string>("  Value:"));
                config[key] = value;
            }
        }

        manifest.Plugins[pluginName] = new PluginDefinition
        {
            Type = type,
            Description = description,
            Config = config
        };

        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

        AnsiConsole.MarkupLine($"[green]Plugin '{pluginName}' ({type}) added to workspace '{settings.Workspace}'.[/]");
        return 0;
    }

    private sealed record PluginTemplate(string Type, string Description, Dictionary<string, string> DefaultConfig);
}

public sealed class WorkspacePluginListCommand : AsyncCommand<WorkspacePluginListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; init; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.json found for '{settings.Workspace}'.[/]");
            return 1;
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = parser.Parse(json);

        if (manifest.Plugins.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No plugins configured in this workspace.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("Type");
        table.AddColumn("Description");
        table.AddColumn("Config");

        foreach (var (name, plugin) in manifest.Plugins)
        {
            var configStr = plugin.Config.Count > 0
                ? string.Join(", ", plugin.Config.Select(kv => $"{kv.Key}={kv.Value}"))
                : "[dim]—[/]";
            table.AddRow(
                name,
                plugin.Type,
                plugin.Description ?? "[dim]—[/]",
                configStr);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class WorkspacePluginRemoveCommand : AsyncCommand<WorkspacePluginRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; init; } = string.Empty;

        [CommandOption("--name <NAME>")]
        [Description("Plugin name to remove")]
        public string? Name { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.json found for '{settings.Workspace}'.[/]");
            return 1;
        }

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = parser.Parse(json);

        if (manifest.Plugins.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No plugins configured in this workspace.[/]");
            return 0;
        }

        var pluginName = settings.Name;
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            pluginName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select plugin to remove:")
                    .AddChoices(manifest.Plugins.Keys));
        }

        if (!manifest.Plugins.Remove(pluginName))
        {
            AnsiConsole.MarkupLine($"[yellow]Plugin '{pluginName}' not found in the workspace.[/]");
            return 1;
        }

        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

        AnsiConsole.MarkupLine($"[green]Plugin '{pluginName}' removed from workspace '{settings.Workspace}'.[/]");
        return 0;
    }
}
