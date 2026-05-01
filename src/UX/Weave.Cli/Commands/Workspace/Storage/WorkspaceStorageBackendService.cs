using System.Net.Sockets;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspaceStorageBackendService(StorageBackendService? storage = null)
{
    private readonly StorageBackendService _storage = storage ?? new StorageBackendService();
    private readonly StorageConnectionStrings _connectionStrings = new();

    public IReadOnlyList<string> SupportedBackends => _storage.SupportedBackends;

    public async Task<bool> CheckDatabaseExistsAsync(string backend, string connectionString, string database, CancellationToken ct)
    {
        if (backend == "sqlite")
        {
            var dataSource = _connectionStrings.TryGetSqliteDataSource(connectionString);
            return dataSource is not null && File.Exists(dataSource);
        }

        try
        {
            var (host, port) = backend switch
            {
                "postgresql" => _storage.ParseKvHostPort(connectionString, "Host", 5432),
                "sqlserver" => _storage.ParseSqlServerHostPort(connectionString),
                _ => (string.Empty, 0)
            };

            if (port == 0)
                return false;

            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, cts.Token);

            return connectionString.Contains($"Database={database}", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains($"Initial Catalog={database}", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string ReplaceDatabaseInConnectionString(string backend, string connectionString, string newDatabase)
    {
        if (backend == "sqlite")
        {
            return _connectionStrings.ReplaceSqliteDataSource(connectionString, newDatabase);
        }

        return _connectionStrings.ReplaceDatabaseName(connectionString, newDatabase);
    }

    public string MaskPassword(string connStr) => _storage.MaskConnectionString(connStr);
}
