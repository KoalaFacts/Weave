using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class InitStoragePrompt
{
    public static async Task<InitStorageSelection> PromptAsync(CancellationToken ct)
    {
        CliTheme.WriteSection("Step 1 · Storage");
        AnsiConsole.MarkupLine("Where should Weave store agent state, skills, and user profiles?");
        AnsiConsole.WriteLine();

        var storage = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Storage backend:")
                .Styled()
                .AddChoices(
                    "sqlite    — local file, zero config, persists across restarts (default)",
                    "postgresql — cross-platform relational, open source",
                    "sqlserver — enterprise SQL Server",
                    "redis     — fast in-memory store, shared across silos",
                    "memory    — no persistence, resets on restart"));

        var storageKey = storage.Split(' ')[0].Trim();
        var connectionString = storageKey switch
        {
            "sqlite" => ConfigureSqlite(),
            "postgresql" or "sqlserver" or "redis" => await ConfigureServerStorageAsync(storageKey, ct),
            _ => null
        };

        return new InitStorageSelection(storageKey, connectionString);
    }

    private static string ConfigureSqlite()
    {
        var weaveHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        var defaultDb = Path.Combine(weaveHome, "weave.db");
        CliTheme.WriteInfo($"Database: {defaultDb}");
        CliTheme.WriteMuted("  Single file, portable, no server needed.");
        return $"Data Source={defaultDb}";
    }

    private static async Task<string?> ConfigureServerStorageAsync(string storageKey, CancellationToken ct)
    {
        var secretMethod = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How would you like to provide the connection string?")
                .Styled()
                .AddChoices(
                    "env      — environment variable (recommended, nothing stored to disk)",
                    "file     — read from a protected file on disk",
                    "vault    — fetch from HashiCorp Vault at startup",
                    "inline   — enter now (stored in config, not recommended for production)"));

        var method = secretMethod.Split(' ')[0].Trim();
        var connectionString = method switch
        {
            "env" => ConfigureEnvConnection(storageKey),
            "file" => ConfigureFileConnection(),
            "vault" => ConfigureVaultConnection(),
            _ => ConfigureInlineConnection(storageKey)
        };

        await TestConnectionAsync(storageKey, connectionString, ct);
        ShowSqlScriptReminder(storageKey);
        return connectionString;
    }

    private static string ConfigureEnvConnection(string storageKey)
    {
        var envRef = CliConfigStore.ToEnvReference(storageKey);
        var envVar = envRef[4..];
        var currentValue = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            CliTheme.WriteSuccess($"Environment variable {envVar} is already set.");
        }
        else
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo("Set the environment variable before starting Weave:");
            CliTheme.WriteMuted($"  export {envVar}=\"Host=localhost;Database=weave;Username=...;Password=...\"");
            CliTheme.WriteMuted("  Add to your shell profile (~/.bashrc, ~/.zshrc) to persist.");
        }

        return envRef;
    }

    private static string ConfigureFileConnection()
    {
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "connection.secret");
        var secretPath = AnsiConsole.Prompt(new TextPrompt<string>("Path to secret file:").Styled().DefaultValue(defaultPath));

        if (!File.Exists(secretPath))
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo("Create the secret file with your connection string:");
            CliTheme.WriteMuted($"  echo 'Host=localhost;Database=weave;...' > {secretPath}");
            CliTheme.WriteMuted($"  chmod 600 {secretPath}");
        }
        else
        {
            CliTheme.WriteSuccess("Secret file found.");
        }

        return $"file:{secretPath}";
    }

    private static string ConfigureVaultConnection()
    {
        var vaultPath = AnsiConsole.Prompt(
            new TextPrompt<string>("Vault secret path:").Styled().DefaultValue("secret/data/weave/connection"));

        var hasAddr = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAULT_ADDR"));
        var hasToken = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAULT_TOKEN"));

        if (hasAddr && hasToken)
        {
            CliTheme.WriteSuccess("VAULT_ADDR and VAULT_TOKEN are set.");
        }
        else
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo("Set these environment variables before starting Weave:");
            if (!hasAddr)
                CliTheme.WriteMuted("  export VAULT_ADDR=\"https://vault.example.com\"");
            if (!hasToken)
                CliTheme.WriteMuted("  export VAULT_TOKEN=\"hvs.your-token\"");
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  Store your connection string in Vault:");
            CliTheme.WriteMuted($"  vault kv put {vaultPath.Replace("secret/data/", "secret/", StringComparison.Ordinal)} value=\"Host=...;Database=weave;...\"");
        }

        return $"vault:{vaultPath}";
    }

    private static string ConfigureInlineConnection(string storageKey)
    {
        CliTheme.WriteWarning("Storing connection strings in config is not recommended for production.");
        var defaultConn = storageKey switch
        {
            "postgresql" => "Host=localhost;Database=weave;Username=;Password=",
            "sqlserver" => "Server=localhost;Database=weave;Trusted_Connection=true;TrustServerCertificate=true",
            "redis" => "localhost:6379",
            _ => ""
        };

        return AnsiConsole.Prompt(new TextPrompt<string>("Connection string:").Styled().DefaultValue(defaultConn));
    }

    private static async Task TestConnectionAsync(string storageKey, string? connectionString, CancellationToken ct)
    {
        string? resolvedConn = null;
        try
        {
            resolvedConn = CliConfigStore.ResolveConnectionString(connectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IOException)
        {
            CliTheme.WriteWarning($"Could not resolve connection string: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(resolvedConn))
            return;

        AnsiConsole.WriteLine();
        var reachable = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Testing connectivity...", async _ => await InitEnvironmentProbe.TestConnectivityAsync(storageKey, resolvedConn, ct));

        if (reachable)
            CliTheme.WriteSuccess("Connection successful.");
        else
            CliTheme.WriteWarning("Could not connect — verify your connection string before starting.");
    }

    private static void ShowSqlScriptReminder(string storageKey)
    {
        if (storageKey is not ("postgresql" or "sqlserver"))
            return;

        AnsiConsole.WriteLine();
        CliTheme.WriteInfo("Orleans requires database tables for actor storage and clustering.");
        CliTheme.WriteMuted("  SQL scripts: https://learn.microsoft.com/dotnet/orleans/host/configuration-guide/adonet-configuration");
        CliTheme.WriteMuted("  Run the Main and Persistence scripts for your database before starting.");
    }
}
