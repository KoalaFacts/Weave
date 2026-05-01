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

        var updated = _values.SetValue(config, options.Key, options.Value);
        if (updated is null)
        {
            CliTheme.WriteError($"Unknown config key '{options.Key}'.");
            CliTheme.WriteMuted("  Valid keys: siloPath, defaultPort");
            return Task.FromResult(1);
        }

        CliConfigStore.Save(updated);
        CliTheme.WriteSuccess($"{options.Key} = {options.Value}");
        return Task.FromResult(0);
    }
}
