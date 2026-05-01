using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class StorageCommands
{
    internal static readonly string[] SupportedBackends = ["memory", "sqlite", "postgresql", "sqlserver", "redis"];

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
        cmd.SetAction((_, cancellationToken) => new StorageShowCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));

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
            return await new StorageChangeCliCommand().ExecuteAsync(new StorageChangeOptions(backend, connectionStr, migrate), cancellationToken);
        });

        return cmd;
    }

    internal static async Task<bool> IsRunningAsync(int port, CancellationToken ct)
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

    internal static async Task<bool> TestConnectivityAsync(string backend, string connectionString, CancellationToken ct)
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

            if (port == 0)
                return false;

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
            if (kv.Length != 2)
                continue;
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
            if (kv.Length != 2)
                continue;
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

    internal static string MaskConnectionString(string connStr)
    {
        if (connStr.Contains("Password", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                connStr, @"(Password\s*=\s*)[^;]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return connStr;
    }

    internal static string DefaultSqlitePath()
    {
        var weaveHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        return Path.Combine(weaveHome, "weave.db");
    }
}
