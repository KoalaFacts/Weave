using System.Collections.Frozen;
using System.CommandLine;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceAddPluginCommand
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

    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Plugin name" };
        var typeOption = new Option<string?>("--type") { Description = "Plugin type (dapr, vault, http, custom)" };
        typeOption.CompletionSources.Add(CliCompletions.CompletePluginTypes);

        var cmd = new Command("plugin", "Add a plugin") { workspaceArg, nameOption, typeOption };
        // Also register as just "add" when used under the plugin branch
        cmd.Aliases.Add("add");
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var pluginName = parseResult.GetValue(nameOption);
            var type = parseResult.GetValue(typeOption);

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(type))
            {
                type = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select plugin type:")
                        .Styled()
                        .AddChoices("dapr", "vault", "http", "custom"));
            }

            if (string.IsNullOrWhiteSpace(pluginName))
            {
                pluginName = AnsiConsole.Prompt(
                    new TextPrompt<string>("Plugin name:")
                        .Styled()
                        .DefaultValue(type));
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Plugins.ContainsKey(pluginName))
            {
                CliTheme.WriteWarning($"Plugin '{pluginName}' already exists in the workspace.");
                return 1;
            }

            Dictionary<string, string> config;
            string? description;

            if (Templates.TryGetValue(type, out var template))
            {
                description = template.Description;
                config = new Dictionary<string, string>(template.DefaultConfig);

                foreach (var key in template.DefaultConfig.Keys.ToArray())
                {
                    config[key] = AnsiConsole.Prompt(
                        new TextPrompt<string>($"  {key}:")
                            .Styled()
                            .DefaultValue(template.DefaultConfig[key]));
                }
            }
            else
            {
                description = AnsiConsole.Prompt(
                    new TextPrompt<string>("Description (optional):")
                        .Styled()
                        .AllowEmpty());
                if (string.IsNullOrWhiteSpace(description))
                    description = null;

                config = [];
                while (AnsiConsole.Confirm("Add a config value?", false))
                {
                    var key = AnsiConsole.Prompt(new TextPrompt<string>("  Key:").Styled());
                    var value = AnsiConsole.Prompt(new TextPrompt<string>("  Value:").Styled());
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

            CliTheme.WriteSuccess($"Plugin '{pluginName}' ({type}) added to workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }

    private sealed record PluginTemplate(string Type, string Description, Dictionary<string, string> DefaultConfig);
}

internal static class WorkspacePluginListCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("list", "List configured plugins") { workspaceArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
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
        });

        return cmd;
    }
}

internal static class WorkspacePluginRemoveCommand
{
    public static Command Create()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var nameOption = new Option<string?>("--name") { Description = "Plugin name to remove" };

        var cmd = new Command("remove", "Remove a plugin") { workspaceArg, nameOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var pluginName = parseResult.GetValue(nameOption);

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            if (manifest.Plugins.Count == 0)
            {
                CliTheme.WriteWarning("No plugins configured in this workspace.");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(pluginName))
            {
                pluginName = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select plugin to remove:")
                        .Styled()
                        .AddChoices(manifest.Plugins.Keys));
            }

            if (!manifest.Plugins.Remove(pluginName))
            {
                CliTheme.WriteWarning($"Plugin '{pluginName}' not found in the workspace.");
                return 1;
            }

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            CliTheme.WriteSuccess($"Plugin '{pluginName}' removed from workspace '{workspace}'.");
            return 0;
        });

        return cmd;
    }
}
