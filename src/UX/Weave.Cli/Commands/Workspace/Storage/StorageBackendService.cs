using System.Net.Sockets;

namespace Weave.Cli.Commands;

internal sealed class StorageBackendService
{
    public IReadOnlyList<string> SupportedBackends { get; } = ["memory", "sqlite", "postgresql", "sqlserver", "redis"];

    public static async Task<bool> IsRunningAsync(int port, CancellationToken ct)
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

    public static async Task<bool> TestConnectivityAsync(string backend, string connectionString, CancellationToken ct)
    {
        try
        {
            var (host, port) = backend switch
            {
                "redis" => StorageConnectionStrings.ParseHostPort(connectionString, 6379),
                "postgresql" => StorageConnectionStrings.ParseKeyValueHostPort(connectionString, "Host", 5432),
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

    public static (string host, int port) ParseHostPort(string connStr, int defaultPort) =>
        StorageConnectionStrings.ParseHostPort(connStr, defaultPort);

    public static (string host, int port) ParseKvHostPort(string connStr, string hostKey, int defaultPort) =>
        StorageConnectionStrings.ParseKeyValueHostPort(connStr, hostKey, defaultPort);

    public static (string host, int port) ParseSqlServerHostPort(string connStr) =>
        StorageConnectionStrings.ParseSqlServerHostPort(connStr);

    public static string MaskConnectionString(string connStr) => StorageConnectionStrings.MaskSecret(connStr);

    public static string DefaultSqlitePath()
    {
        var weaveHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        return Path.Combine(weaveHome, "weave.db");
    }
}
