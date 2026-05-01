using System.Collections.Frozen;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceAddPluginCliCommand : ICliCommand<WorkspaceAddPluginOptions>
{
    private static readonly FrozenDictionary<string, PluginTemplate> Templates =
        new Dictionary<string, PluginTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["dapr"] = new("dapr", "Dapr sidecar for pub/sub events and service invocation", new Dictionary<string, string> { ["port"] = "3500" }),
            ["vault"] = new("vault", "HashiCorp Vault for production secret management", new Dictionary<string, string> { ["address"] = "http://localhost:8200" }),
            ["http"] = new("http", "Generic HTTP/REST endpoint", new Dictionary<string, string> { ["base_url"] = "http://localhost:8080" }),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public string Name => "plugin";

    public IReadOnlyList<string> Aliases => ["add"];

    public string Description => "Add a plugin";

    public async Task<int> ExecuteAsync(WorkspaceAddPluginOptions options, CancellationToken ct)
    {
        var pluginName = options.PluginName;
        var type = options.Type;

        var manifestPath = ManifestResolver.Resolve(options.Workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Workspace}'.");
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
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = parser.Parse(json);

        if (manifest.Plugins.ContainsKey(pluginName))
        {
            CliTheme.WriteWarning($"Plugin '{pluginName}' already exists in the workspace.");
            return 1;
        }

        var (description, config) = PromptPluginConfiguration(type);

        manifest.Plugins[pluginName] = new PluginDefinition
        {
            Type = type,
            Description = description,
            Config = config
        };

        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), ct);

        CliTheme.WriteSuccess($"Plugin '{pluginName}' ({type}) added to workspace '{options.Workspace}'.");
        return 0;
    }

    private static (string? Description, Dictionary<string, string> Config) PromptPluginConfiguration(string type)
    {
        if (Templates.TryGetValue(type, out var template))
        {
            var config = new Dictionary<string, string>(template.DefaultConfig);
            foreach (var key in template.DefaultConfig.Keys.ToArray())
            {
                config[key] = AnsiConsole.Prompt(
                    new TextPrompt<string>($"  {key}:")
                        .Styled()
                        .DefaultValue(template.DefaultConfig[key]));
            }

            return (template.Description, config);
        }

        var description = AnsiConsole.Prompt(new TextPrompt<string>("Description (optional):").Styled().AllowEmpty());
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        var customConfig = new Dictionary<string, string>();
        while (AnsiConsole.Confirm("Add a config value?", false))
        {
            var key = AnsiConsole.Prompt(new TextPrompt<string>("  Key:").Styled());
            var value = AnsiConsole.Prompt(new TextPrompt<string>("  Value:").Styled());
            customConfig[key] = value;
        }

        return (description, customConfig);
    }
}
