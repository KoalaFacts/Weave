using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using Spectre.Console;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal static class InitCommand
{
    public static Command Create()
    {
        var cmd = new Command("init", "Set up the Weave environment on this machine");
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            CliTheme.WriteBanner();
            AnsiConsole.MarkupLine("[bold]Setting up Weave on this machine.[/]");
            AnsiConsole.WriteLine();

            if (CliConfigStore.Exists())
            {
                var existing = CliConfigStore.Load();
                CliTheme.WriteInfo("Existing configuration found:");
                CliTheme.WriteKeyValue("Storage", existing.Storage);
                CliTheme.WriteKeyValue("Port", existing.DefaultPort.ToString(CultureInfo.InvariantCulture));
                CliTheme.WriteKeyValue("Silo path", existing.SiloPath ?? "(auto-detect)");
                AnsiConsole.WriteLine();

                if (!AnsiConsole.Confirm("Reconfigure?", defaultValue: false))
                    return 0;

                AnsiConsole.WriteLine();
            }

            // ── Step 1: Storage backend ──────────────────────────────
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

            string? connectionString = null;

            if (storageKey == "sqlite")
            {
                var weaveHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
                var defaultDb = Path.Combine(weaveHome, "weave.db");
                connectionString = $"Data Source={defaultDb}";
                CliTheme.WriteInfo($"Database: {defaultDb}");
                CliTheme.WriteMuted("  Single file, portable, no server needed.");
            }
            else if (storageKey is "postgresql" or "sqlserver" or "redis")
            {
                var defaultConn = storageKey switch
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

                AnsiConsole.WriteLine();
                var reachable = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Testing connectivity...", async _ =>
                    {
                        return await TestConnectivityAsync(storageKey, connectionString, cancellationToken);
                    });

                if (reachable)
                    CliTheme.WriteSuccess("Connection successful.");
                else
                {
                    CliTheme.WriteWarning("Could not connect. Saving config anyway — fix later.");
                    CliTheme.WriteMuted("  Update with: weave config set connectionString \"<your-connection-string>\"");
                }

                if (storageKey is "postgresql" or "sqlserver")
                {
                    AnsiConsole.WriteLine();
                    CliTheme.WriteInfo("Orleans requires database tables for grain storage and clustering.");
                    CliTheme.WriteMuted("  SQL scripts: https://learn.microsoft.com/dotnet/orleans/host/configuration-guide/adonet-configuration");
                    CliTheme.WriteMuted("  Run the Main and Persistence scripts for your database before starting.");
                }
            }

            // ── Step 2: Server port ──────────────────────────────────
            AnsiConsole.WriteLine();
            CliTheme.WriteSection("Step 2 · Server");

            var port = AnsiConsole.Prompt(
                new TextPrompt<int>("Server port:")
                    .Styled()
                    .DefaultValue(WeavePorts.SiloHttp));

            // ── Step 3: Silo path ────────────────────────────────────
            AnsiConsole.WriteLine();
            CliTheme.WriteSection("Step 3 · Runtime");

            var detectedSilo = DetectSiloPath();
            string? siloPath;

            if (detectedSilo is not null)
            {
                CliTheme.WriteInfo($"Detected runtime at: {detectedSilo}");
                siloPath = AnsiConsole.Confirm("Use this path?")
                    ? detectedSilo
                    : PromptSiloPath();
            }
            else
            {
                CliTheme.WriteMuted("No runtime detected in the current directory.");
                siloPath = PromptSiloPath();
            }

            // ── Save ─────────────────────────────────────────────────
            var config = new CliConfig
            {
                SiloPath = siloPath,
                DefaultPort = port,
                Storage = storageKey,
                ConnectionString = connectionString
            };

            CliConfigStore.Save(config);

            AnsiConsole.WriteLine();
            CliTheme.WriteSuccess("Environment configured.");
            CliTheme.WriteKeyValue("Config saved to", "~/.weave/config.json");
            CliTheme.WriteKeyValue("Storage", storageKey);
            CliTheme.WriteKeyValue("Port", port.ToString(CultureInfo.InvariantCulture));
            if (siloPath is not null)
                CliTheme.WriteKeyValue("Runtime", siloPath);

            // ── Next steps ───────────────────────────────────────────
            AnsiConsole.WriteLine();
            CliTheme.WriteSection("Next steps");
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  1. Create a workspace:");
            CliTheme.WriteMuted("     weave workspace new my-app --preset coding-assistant");
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  2. Run it:");
            CliTheme.WriteMuted("     weave run my-app");
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  Or try the full-featured preset:");
            CliTheme.WriteMuted("     weave workspace new support --preset support-team");
            CliTheme.WriteMuted("     weave run support");
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  Browse the marketplace:");
            CliTheme.WriteMuted("     weave marketplace list");

            return 0;
        });

        return cmd;
    }

    private static async Task<bool> TestConnectivityAsync(string storageKey, string connectionString, CancellationToken ct)
    {
        try
        {
            var (host, port) = storageKey switch
            {
                "redis" => ParseHostPort(connectionString, 6379),
                "postgresql" => ParsePgHostPort(connectionString),
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

    private static (string host, int port) ParseHostPort(string connStr, int defaultPort)
    {
        var parts = connStr.Split(',')[0].Split(':');
        var host = parts[0].Trim();
        var port = parts.Length > 1 && int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var p) ? p : defaultPort;
        return (host, port);
    }

    private static (string host, int port) ParsePgHostPort(string connStr)
    {
        var host = "localhost";
        var port = 5432;

        foreach (var part in connStr.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var val = kv[1].Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                host = val;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, CultureInfo.InvariantCulture, out var p))
                port = p;
        }

        return (host, port);
    }

    private static (string host, int port) ParseSqlServerHostPort(string connStr)
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

    private static string? PromptSiloPath()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("Path to Weave runtime (or press Enter to auto-detect later):")
                .Styled()
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = Path.GetFullPath(path);

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            CliTheme.WriteWarning($"Path does not exist: {path}");
            CliTheme.WriteMuted("  Saving anyway — fix later with: weave config set siloPath <path>");
        }

        return path;
    }

    private static string? DetectSiloPath()
    {
        var candidates = new[]
        {
            Path.Combine("src", "Runtime", "Weave.Silo"),
            Path.Combine("src", "Runtime", "Weave.Silo", "Weave.Silo.csproj")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var exeDir = AppContext.BaseDirectory;
        var siloDll = Path.Combine(exeDir, "Weave.Silo.dll");
        if (File.Exists(siloDll))
            return siloDll;

        return null;
    }
}
