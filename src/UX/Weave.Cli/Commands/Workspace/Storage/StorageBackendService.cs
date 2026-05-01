using System.Globalization;
using System.Net.Sockets;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class StorageBackendService
{
    public IReadOnlyList<string> SupportedBackends { get; } = ["memory", "sqlite", "postgresql", "sqlserver", "redis"];

    public async Task<bool> IsRunningAsync(int port, CancellationToken ct)
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

    public async Task<bool> TestConnectivityAsync(string backend, string connectionString, CancellationToken ct)
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

    public (string host, int port) ParseHostPort(string connStr, int defaultPort)
    {
        var parts = connStr.Split(',')[0].Split(':');
        var host = parts[0].Trim();
        var port = parts.Length > 1 && int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultPort;
        return (host, port);
    }

    public (string host, int port) ParseKvHostPort(string connStr, string hostKey, int defaultPort)
    {
        var host = "localhost";
        var port = defaultPort;

        foreach (var part in connStr.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;

            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key.Equals(hostKey, StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                host = value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                port = parsed;
        }

        return (host, port);
    }

    public (string host, int port) ParseSqlServerHostPort(string connStr)
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

    public string MaskConnectionString(string connStr)
    {
        if (connStr.Contains("Password", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                connStr, @"(Password\s*=\s*)[^;]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return connStr;
    }

    public string DefaultSqlitePath()
    {
        var weaveHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        return Path.Combine(weaveHome, "weave.db");
    }
}
