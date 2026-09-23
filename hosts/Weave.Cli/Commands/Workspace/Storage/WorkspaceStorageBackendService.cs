using System.Net.Sockets;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceStorageBackendService(StorageBackendService? storage = null)
{
    private readonly StorageBackendService _storage = storage ?? new StorageBackendService();

    public IReadOnlyList<string> SupportedBackends => _storage.SupportedBackends;

    public static async Task<bool> CheckDatabaseExistsAsync(string backend, string connectionString, string database, CancellationToken ct)
    {
        if (backend == "sqlite")
        {
            var dataSource = StorageConnectionStrings.TryGetSqliteDataSource(connectionString);
            return dataSource is not null && File.Exists(dataSource);
        }

        try
        {
            var (host, port) = backend switch
            {
                "postgresql" => StorageBackendService.ParseKvHostPort(connectionString, "Host", 5432),
                "sqlserver" => StorageBackendService.ParseSqlServerHostPort(connectionString),
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
        catch (Exception ex) when (ex is SocketException or TaskCanceledException or IOException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    public static string ReplaceDatabaseInConnectionString(string backend, string connectionString, string newDatabase)
    {
        if (backend == "sqlite")
        {
            return StorageConnectionStrings.ReplaceSqliteDataSource(connectionString, newDatabase);
        }

        return StorageConnectionStrings.ReplaceDatabaseName(connectionString, newDatabase);
    }

    public static string MaskPassword(string connStr) => StorageBackendService.MaskConnectionString(connStr);
}
