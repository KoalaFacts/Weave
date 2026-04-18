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
                var envRef = CliConfigStore.ToEnvReference(storageKey);
                var envVar = envRef[4..];

                if (method == "env")
                {
                    connectionString = envRef;

                    var currentValue = Environment.GetEnvironmentVariable(envVar);
                    if (!string.IsNullOrWhiteSpace(currentValue))
                    {
                        CliTheme.WriteSuccess($"Environment variable {envVar} is already set.");
                    }
                    else
                    {
                        AnsiConsole.WriteLine();
                        CliTheme.WriteInfo($"Set the environment variable before starting Weave:");
                        CliTheme.WriteMuted($"  export {envVar}=\"Host=localhost;Database=weave;Username=...;Password=...\"");
                        CliTheme.WriteMuted($"  Add to your shell profile (~/.bashrc, ~/.zshrc) to persist.");
                    }
                }
                else if (method == "file")
                {
                    var defaultPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".weave", "connection.secret");

                    var secretPath = AnsiConsole.Prompt(
                        new TextPrompt<string>("Path to secret file:")
                            .Styled()
                            .DefaultValue(defaultPath));

                    connectionString = $"file:{secretPath}";

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
                }
                else if (method == "vault")
                {
                    var vaultPath = AnsiConsole.Prompt(
                        new TextPrompt<string>("Vault secret path:")
                            .Styled()
                            .DefaultValue("secret/data/weave/connection"));

                    connectionString = $"vault:{vaultPath}";

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
                }
                else
                {
                    CliTheme.WriteWarning("Storing connection strings in config is not recommended for production.");
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
                }

                // Test connectivity if we can resolve the value
                string? resolvedConn = null;
                try { resolvedConn = CliConfigStore.ResolveConnectionString(connectionString); }
                catch { }

                if (!string.IsNullOrWhiteSpace(resolvedConn))
                {
                    AnsiConsole.WriteLine();
                    var reachable = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Testing connectivity...", async _ =>
                            await TestConnectivityAsync(storageKey, resolvedConn, cancellationToken));

                    if (reachable)
                        CliTheme.WriteSuccess("Connection successful.");
                    else
                        CliTheme.WriteWarning("Could not connect — verify your connection string before starting.");
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

            // ── Step 4: Security ─────────────────────────────────────
            AnsiConsole.WriteLine();
            CliTheme.WriteSection("Step 4 · Security");
            AnsiConsole.MarkupLine("API authentication protects your agents and data from unauthorized access.");
            AnsiConsole.WriteLine();

            var authChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("API authentication:")
                    .Styled()
                    .AddChoices(
                        "none     — no auth, open access (local dev only)",
                        "apikey   — require X-Api-Key header on every request",
                        "bearer   — require Authorization: Bearer token on every request"));

            var authMode = authChoice.Split(' ')[0].Trim();
            string? authSecret = null;

            if (authMode is "apikey" or "bearer")
            {
                var authSecretMethod = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Where should the API secret come from?")
                        .Styled()
                        .AddChoices(
                            "env      — environment variable",
                            "file     — protected file on disk",
                            "vault    — HashiCorp Vault",
                            "generate — generate a random key now"));

                var authMethod = authSecretMethod.Split(' ')[0].Trim();

                if (authMethod == "env")
                {
                    authSecret = "env:WEAVE_API_SECRET";
                    var exists = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEAVE_API_SECRET"));
                    if (exists)
                        CliTheme.WriteSuccess("WEAVE_API_SECRET is set.");
                    else
                    {
                        CliTheme.WriteInfo("Set before starting:");
                        CliTheme.WriteMuted("  export WEAVE_API_SECRET=\"your-secret-key\"");
                    }
                }
                else if (authMethod == "file")
                {
                    var defaultPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".weave", "api.secret");
                    var secretPath = AnsiConsole.Prompt(
                        new TextPrompt<string>("Path to secret file:")
                            .Styled()
                            .DefaultValue(defaultPath));
                    authSecret = $"file:{secretPath}";

                    if (!File.Exists(secretPath))
                    {
                        CliTheme.WriteInfo("Create the file with your API secret:");
                        CliTheme.WriteMuted($"  openssl rand -hex 32 > {secretPath}");
                        CliTheme.WriteMuted($"  chmod 600 {secretPath}");
                    }
                }
                else if (authMethod == "vault")
                {
                    var vaultPath = AnsiConsole.Prompt(
                        new TextPrompt<string>("Vault secret path:")
                            .Styled()
                            .DefaultValue("secret/data/weave/api-key"));
                    authSecret = $"vault:{vaultPath}";
                }
                else
                {
                    var generated = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                    authSecret = $"env:WEAVE_API_SECRET";
                    CliTheme.WriteSuccess("Generated API key. Set it before starting:");
                    CliTheme.WriteMuted($"  export WEAVE_API_SECRET=\"{generated}\"");
                }
            }

            var requireHttps = false;
            if (authMode is not "none")
            {
                requireHttps = AnsiConsole.Confirm("Require HTTPS?", defaultValue: false);
            }

            // ── Save ─────────────────────────────────────────────────
            var config = new CliConfig
            {
                SiloPath = siloPath,
                DefaultPort = port,
                Storage = storageKey,
                ConnectionString = connectionString,
                AuthMode = authMode,
                AuthSecret = authSecret,
                RequireHttps = requireHttps
            };

            CliConfigStore.Save(config);

            AnsiConsole.WriteLine();
            CliTheme.WriteSuccess("Environment configured.");
            CliTheme.WriteKeyValue("Config saved to", "~/.weave/config.json");
            CliTheme.WriteKeyValue("Storage", storageKey);
            CliTheme.WriteKeyValue("Port", port.ToString(CultureInfo.InvariantCulture));
            CliTheme.WriteKeyValue("Auth", authMode);
            if (requireHttps)
                CliTheme.WriteKeyValue("HTTPS", "enforced");
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
