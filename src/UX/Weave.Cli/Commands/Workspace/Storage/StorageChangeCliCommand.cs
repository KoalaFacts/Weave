using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class StorageChangeCliCommand(
    StorageBackendService? storage = null,
    StorageChangePrompt? prompt = null) : ICliCommand<StorageChangeOptions>
{
    private readonly StorageBackendService _storage = storage ?? new StorageBackendService();
    private readonly StorageChangePrompt _prompt = prompt ?? new StorageChangePrompt(storage);

    public string Name => "change";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Switch to a different storage backend";

    public async Task<int> ExecuteAsync(StorageChangeOptions options, CancellationToken ct)
    {
        var currentConfig = CliConfigStore.Load();

        var isRunning = await _storage.IsRunningAsync(currentConfig.DefaultPort, ct);
        if (isRunning)
        {
            CliTheme.WriteError("Server is still running. Stop it first:");
            CliTheme.WriteMuted("  weave workspace down <name>");
            CliTheme.WriteMuted("  Then re-run: weave storage change");
            return 1;
        }

        CliTheme.WriteSection("Change Storage Backend");
        CliTheme.WriteKeyValue("Current", currentConfig.Storage);
        AnsiConsole.WriteLine();

        var backend = _prompt.SelectBackend(options.Backend);

        if (!_storage.SupportedBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
        {
            CliTheme.WriteError($"Unknown backend '{backend}'. Supported: {string.Join(", ", _storage.SupportedBackends)}");
            return 1;
        }

        if (string.Equals(backend, currentConfig.Storage, StringComparison.OrdinalIgnoreCase))
        {
            CliTheme.WriteWarning($"Already using '{backend}'.");
            return 0;
        }

        var connectionStr = await _prompt.ResolveConnectionStringAsync(backend, options.ConnectionString, ct);

        var newConfig = currentConfig with
        {
            Storage = backend,
            ConnectionString = backend == "memory" ? null : connectionStr
        };
        CliConfigStore.Save(newConfig);

        AnsiConsole.WriteLine();
        CliTheme.WriteSuccess($"Storage changed: {currentConfig.Storage} → {backend}");

        if (options.Migrate)
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo("Use 'weave data import' to restore your exported data on the new backend.");
        }
        else if (currentConfig.Storage != "memory")
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  To migrate existing data:");
            CliTheme.WriteMuted("  1. weave data export <workspace> -o backup.json  (before changing)");
            CliTheme.WriteMuted("  2. weave data import backup.json                 (after changing)");
        }

        return 0;
    }
}
