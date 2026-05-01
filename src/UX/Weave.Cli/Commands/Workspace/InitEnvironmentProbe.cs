using System.Globalization;
using System.Net.Sockets;
using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class InitEnvironmentProbe
{
    public async Task<bool> TestConnectivityAsync(string storageKey, string connectionString, CancellationToken ct)
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

    public string? PromptSiloPath()
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

    public string? DetectSiloPath()
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

        var siloDll = Path.Combine(AppContext.BaseDirectory, "Weave.Silo.dll");
        return File.Exists(siloDll) ? siloDll : null;
    }

    private static (string host, int port) ParseHostPort(string connStr, int defaultPort)
    {
        var parts = connStr.Split(',')[0].Split(':');
        var host = parts[0].Trim();
        var port = parts.Length > 1 && int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultPort;
        return (host, port);
    }

    private static (string host, int port) ParsePgHostPort(string connStr)
    {
        var host = "localhost";
        var port = 5432;

        foreach (var part in connStr.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;
            var key = kv[0].Trim();
            var value = kv[1].Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                host = value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                port = parsed;
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
            if (kv.Length != 2)
                continue;
            var key = kv[0].Trim();
            var value = kv[1].Trim();

            if (!key.Equals("Server", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                continue;

            var serverParts = value.Split(',', 2);
            host = serverParts[0].Trim();
            if (serverParts.Length > 1 && int.TryParse(serverParts[1].Trim(), CultureInfo.InvariantCulture, out var parsed))
                port = parsed;
        }

        return (host, port);
    }
}
