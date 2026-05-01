using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class ConfigSetCliCommand(ConfigValueAccessor? values = null) : ICliCommand<ConfigSetOptions>
{
    private readonly ConfigValueAccessor _values = values ?? new ConfigValueAccessor();

    public string Name => "set";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Update a configuration value";

    public Task<int> ExecuteAsync(ConfigSetOptions options, CancellationToken ct)
    {
        var config = CliConfigStore.Load();
        var key = SelectKey(options.Key);
        var value = SelectValue(options.Value, key);

        var updated = _values.SetValue(config, key, value);
        if (updated is null)
        {
            CliTheme.WriteError($"Unknown config key '{key}'.");
            CliTheme.WriteMuted("  Valid keys: siloPath, defaultPort");
            return Task.FromResult(1);
        }

        CliConfigStore.Save(updated);
        CliTheme.WriteSuccess($"{key} = {value}");
        return Task.FromResult(0);
    }

    private static string SelectKey(string? key) => !string.IsNullOrWhiteSpace(key)
        ? key
        : AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which config value would you like to update?")
                .Styled()
                .AddChoices("siloPath", "defaultPort"));

    private static string SelectValue(string? value, string key) => !string.IsNullOrWhiteSpace(value)
        ? value
        : AnsiConsole.Prompt(new TextPrompt<string>($"{key}:").Styled());
}
