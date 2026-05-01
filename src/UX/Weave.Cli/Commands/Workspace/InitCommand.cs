using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class InitCommand
{
    public static Command Create()
    {
        var cmd = new Command("init", "Set up the Weave environment on this machine");
        cmd.SetAction(async (_, cancellationToken) =>
            await new InitCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));
        return cmd;
    }

    internal static async Task<bool> TestConnectivityAsync(string storageKey, string connectionString, CancellationToken ct)
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

    internal static (string host, int port) ParsePgHostPort(string connStr)
    {
        var host = "localhost";
        var port = 5432;

        foreach (var part in connStr.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;
            var key = kv[0].Trim();
            var val = kv[1].Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
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

    internal static string? PromptSiloPath()
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

    internal static string? DetectSiloPath()
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
