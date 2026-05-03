using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class StorageChangePrompt(StorageBackendService? storage = null)
{
    private readonly StorageBackendService _storage = storage ?? new StorageBackendService();

    public string SelectBackend(string? backend) => !string.IsNullOrWhiteSpace(backend)
        ? backend
        : AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("New backend:")
                .Styled()
                .AddChoices(_storage.SupportedBackends));

    public static async Task<string?> ResolveConnectionStringAsync(string backend, string? connectionString, CancellationToken ct)
    {
        if (backend is "sqlite" && string.IsNullOrWhiteSpace(connectionString))
        {
            var defaultPath = StorageBackendService.DefaultSqlitePath();
            CliTheme.WriteInfo($"Database: {defaultPath}");
            return $"Data Source={defaultPath}";
        }

        if (backend is not ("postgresql" or "sqlserver" or "redis"))
            return connectionString;

        return await PromptConnectionStringAsync(backend, connectionString, ct);
    }

    private static async Task<string?> PromptConnectionStringAsync(string backend, string? connectionString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var defaultConn = backend switch
            {
                "postgresql" => "Host=localhost;Database=weave;Username=;Password=",
                "sqlserver" => "Server=localhost;Database=weave;Trusted_Connection=true;TrustServerCertificate=true",
                "redis" => "localhost:6379",
                _ => ""
            };

            connectionString = AnsiConsole.Prompt(
                new TextPrompt<string>("Connection string:")
                    .Styled()
                    .DefaultValue(defaultConn));
        }

        AnsiConsole.WriteLine();
        var reachable = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Testing connectivity...", async _ => await StorageBackendService.TestConnectivityAsync(backend, connectionString, ct));

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

        return connectionString;
    }
}
