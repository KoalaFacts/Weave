using System.Net.Sockets;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class StorageBackendService(StorageConnectionStrings? connectionStrings = null)
{
    private readonly StorageConnectionStrings _connectionStrings = connectionStrings ?? new StorageConnectionStrings();

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
                "redis" => _connectionStrings.ParseHostPort(connectionString, 6379),
                "postgresql" => _connectionStrings.ParseKeyValueHostPort(connectionString, "Host", 5432),
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

    public (string host, int port) ParseHostPort(string connStr, int defaultPort) =>
        _connectionStrings.ParseHostPort(connStr, defaultPort);

    public (string host, int port) ParseKvHostPort(string connStr, string hostKey, int defaultPort) =>
        _connectionStrings.ParseKeyValueHostPort(connStr, hostKey, defaultPort);

    public (string host, int port) ParseSqlServerHostPort(string connStr) =>
        _connectionStrings.ParseSqlServerHostPort(connStr);

    public string MaskConnectionString(string connStr) => _connectionStrings.MaskSecret(connStr);

    public string DefaultSqlitePath()
    {
        var weaveHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        return Path.Combine(weaveHome, "weave.db");
    }
}
