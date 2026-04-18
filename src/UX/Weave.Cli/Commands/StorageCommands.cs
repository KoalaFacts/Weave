using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class StorageCommands
{
    private static readonly string[] SupportedBackends = ["memory", "sqlite", "postgresql", "sqlserver", "redis"];

    public static Command Create()
    {
        var cmd = new Command("storage", "View and change the storage backend");

        cmd.Subcommands.Add(CreateShowCommand());
        cmd.Subcommands.Add(CreateChangeCommand());

        return cmd;
    }

    private static Command CreateShowCommand()
    {
        var cmd = new Command("show", "Show the current storage configuration");
        cmd.SetAction(_ =>
        {
            var config = CliConfigStore.Load();

            CliTheme.WriteSection("Storage Configuration");
            CliTheme.WriteKeyValue("Backend", config.Storage);

            if (!string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                var masked = MaskConnectionString(config.ConnectionString);
                CliTheme.WriteKeyValue("Connection", masked);
            }
            else
            {
                CliTheme.WriteKeyValue("Connection", config.Storage == "memory" ? "(none — in-memory)" : "(not configured)");
            }

            if (config.Storage == "sqlite" && string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                var defaultDb = DefaultSqlitePath();
                CliTheme.WriteKeyValue("Default DB", defaultDb);
            }

            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  Change with: weave storage change <backend>");
            return 0;
        });

        return cmd;
    }

    private static Command CreateChangeCommand()
    {
        var backendArg = new Argument<string?>("backend")
        {
            Description = "Target backend (memory, sqlite, postgresql, sqlserver, redis)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var connectionOption = new Option<string?>("--connection") { Description = "Connection string for the new backend" };
        var migrateOption = new Option<bool>("--migrate") { Description = "Export data before switching and import after" };

        var cmd = new Command("change", "Switch to a different storage backend") { backendArg, connectionOption, migrateOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var backend = parseResult.GetValue(backendArg);
            var connectionStr = parseResult.GetValue(connectionOption);
            var migrate = parseResult.GetValue(migrateOption);

            var currentConfig = CliConfigStore.Load();

            // Check if server is running
            var isRunning = await IsRunningAsync(currentConfig.DefaultPort, cancellationToken);
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
                        .AddChoices(SupportedBackends));
            }

            if (!SupportedBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
            {
                CliTheme.WriteError($"Unknown backend '{backend}'. Supported: {string.Join(", ", SupportedBackends)}");
                return 1;
            }

            if (string.Equals(backend, currentConfig.Storage, StringComparison.OrdinalIgnoreCase))
            {
                CliTheme.WriteWarning($"Already using '{backend}'.");
                return 0;
            }

            // Connection string
            if (backend is "sqlite" && string.IsNullOrWhiteSpace(connectionStr))
            {
                connectionStr = $"Data Source={DefaultSqlitePath()}";
                CliTheme.WriteInfo($"Database: {DefaultSqlitePath()}");
            }
            else if (backend is "postgresql" or "sqlserver" or "redis")
            {
                if (string.IsNullOrWhiteSpace(connectionStr))
                {
                    var defaultConn = backend switch
                    {
                        "postgresql" => "Host=localhost;Database=weave;Username=weave;Password=weave",
                        "sqlserver" => "Server=localhost;Database=weave;Trusted_Connection=true;TrustServerCertificate=true",
                        "redis" => "localhost:6379",
                        _ => ""
                    };

                    connectionStr = AnsiConsole.Prompt(
                        new TextPrompt<string>("Connection string:")
                            .Styled()
                            .DefaultValue(defaultConn));
                }

                // Test connectivity
                AnsiConsole.WriteLine();
                var reachable = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Testing connectivity...", async _ =>
                        await TestConnectivityAsync(backend, connectionStr, cancellationToken));

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
            }

            // Save
            var newConfig = currentConfig with
            {
                Storage = backend,
                ConnectionString = backend == "memory" ? null : connectionStr
            };
            CliConfigStore.Save(newConfig);

            AnsiConsole.WriteLine();
            CliTheme.WriteSuccess($"Storage changed: {currentConfig.Storage} → {backend}");

            if (migrate)
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
        });

        return cmd;
    }

    private static async Task<bool> IsRunningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"http://localhost:{port}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TestConnectivityAsync(string backend, string connectionString, CancellationToken ct)
    {
        try
        {
            var (host, port) = backend switch
            {
                "redis" => ParseHostPort(connectionString, 6379),
                "postgresql" => ParseKvHostPort(connectionString, "Host", 5432),
                "sqlserver" => ParseSqlServerHostPort(connectionString),
                _ => ("localhost", 0)
            };

            if (port == 0) return false;

            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static (string host, int port) ParseHostPort(string connStr, int defaultPort)
    {
        var parts = connStr.Split(',')[0].Split(':');
        var host = parts[0].Trim();
        var port = parts.Length > 1 && int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var p) ? p : defaultPort;
        return (host, port);
    }

    internal static (string host, int port) ParseKvHostPort(string connStr, string hostKey, int defaultPort)
    {
        var host = "localhost";
        var port = defaultPort;

        foreach (var part in connStr.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var val = kv[1].Trim();

            if (key.Equals(hostKey, StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                host = val;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, CultureInfo.InvariantCulture, out var p))
                port = p;
        }

        return (host, port);
    }

    internal static (string host, int port) ParseSqlServerHostPort(string connStr)
    {
        var host = "localhost";
        var port = 1433;

        foreach (var part in connStr.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var val = kv[1].Trim();

            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
            {
                var serverParts = val.Split(',', 2);
                host = serverParts[0].Trim();
                if (serverParts.Length > 1 && int.TryParse(serverParts[1].Trim(), CultureInfo.InvariantCulture, out var p))
                    port = p;
            }
        }

        return (host, port);
    }

    private static string MaskConnectionString(string connStr)
    {
        if (connStr.Contains("Password", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                connStr, @"(Password\s*=\s*)[^;]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return connStr;
    }

    private static string DefaultSqlitePath()
    {
        var weaveHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        return Path.Combine(weaveHome, "weave.db");
    }
}
