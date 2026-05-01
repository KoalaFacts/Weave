using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspacePluginPrompt(PluginTemplateCatalog? catalog = null)
{
    private readonly PluginTemplateCatalog _catalog = catalog ?? new PluginTemplateCatalog();

    public string SelectType(string? type) => !string.IsNullOrWhiteSpace(type)
        ? type
        : AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select plugin type:")
                .Styled()
                .AddChoices(_catalog.PluginTypes));

    public string SelectName(string? pluginName, string type) => !string.IsNullOrWhiteSpace(pluginName)
        ? pluginName
        : AnsiConsole.Prompt(
            new TextPrompt<string>("Plugin name:")
                .Styled()
                .DefaultValue(type));

    public PluginConfiguration Configure(string type)
    {
        if (_catalog.TryGet(type, out var template))
            return ConfigureTemplate(template);

        return ConfigureCustom();
    }

    private static PluginConfiguration ConfigureTemplate(PluginTemplate template)
    {
        var config = new Dictionary<string, string>(template.DefaultConfig);
        foreach (var key in template.DefaultConfig.Keys.ToArray())
        {
            config[key] = AnsiConsole.Prompt(
                new TextPrompt<string>($"  {key}:")
                    .Styled()
                    .DefaultValue(template.DefaultConfig[key]));
        }

        return new PluginConfiguration(template.Description, config);
    }

    private static PluginConfiguration ConfigureCustom()
    {
        var description = AnsiConsole.Prompt(new TextPrompt<string>("Description (optional):").Styled().AllowEmpty());
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        var config = new Dictionary<string, string>();
        while (AnsiConsole.Confirm("Add a config value?", false))
        {
            var key = AnsiConsole.Prompt(new TextPrompt<string>("  Key:").Styled());
            var value = AnsiConsole.Prompt(new TextPrompt<string>("  Value:").Styled());
            config[key] = value;
        }

        return new PluginConfiguration(description, config);
    }
}
