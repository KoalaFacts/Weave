using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class StorageChangeCliCommand(StorageBackendService? storage = null) : ICliCommand<StorageChangeOptions>
{
    private readonly StorageBackendService _storage = storage ?? new StorageBackendService();

    public string Name => "change";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Switch to a different storage backend";

    public async Task<int> ExecuteAsync(StorageChangeOptions options, CancellationToken ct)
    {
        var backend = options.Backend;
        var connectionStr = options.ConnectionString;
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

        if (string.IsNullOrWhiteSpace(backend))
        {
            backend = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("New backend:")
                    .Styled()
                    .AddChoices(_storage.SupportedBackends));
        }

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

        if (backend is "sqlite" && string.IsNullOrWhiteSpace(connectionStr))
        {
            connectionStr = $"Data Source={_storage.DefaultSqlitePath()}";
            CliTheme.WriteInfo($"Database: {_storage.DefaultSqlitePath()}");
        }
        else if (backend is "postgresql" or "sqlserver" or "redis")
        {
            connectionStr = await PromptConnectionStringAsync(backend, connectionStr, ct);
        }

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

    private async Task<string?> PromptConnectionStringAsync(string backend, string? connectionStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionStr))
        {
            var defaultConn = backend switch
            {
                "postgresql" => "Host=localhost;Database=weave;Username=;Password=",
                "sqlserver" => "Server=localhost;Database=weave;Trusted_Connection=true;TrustServerCertificate=true",
                "redis" => "localhost:6379",
                _ => ""
            };

            connectionStr = AnsiConsole.Prompt(
                new TextPrompt<string>("Connection string:")
                    .Styled()
                    .DefaultValue(defaultConn));
        }

        AnsiConsole.WriteLine();
        var reachable = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Testing connectivity...", async _ => await _storage.TestConnectivityAsync(backend, connectionStr, ct));

        if (reachable)
            CliTheme.WriteSuccess("Connection successful.");
        else
            CliTheme.WriteWarning("Could not connect — saving anyway.");

        if (backend is "postgresql" or "sqlserver")
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo("Run the Orleans SQL scripts before starting:");
            CliTheme.WriteMuted("  https://learn.microsoft.com/dotnet/orleans/host/configuration-guide/adonet-configuration");
        }

        return connectionStr;
    }
}
