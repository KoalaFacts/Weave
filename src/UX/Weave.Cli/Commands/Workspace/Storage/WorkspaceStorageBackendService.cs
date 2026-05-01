using System.Net.Sockets;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspaceStorageBackendService(StorageBackendService? storage = null)
{
    private readonly StorageBackendService _storage = storage ?? new StorageBackendService();

    public IReadOnlyList<string> SupportedBackends => _storage.SupportedBackends;

    public async Task<bool> CheckDatabaseExistsAsync(string backend, string connectionString, string database, CancellationToken ct)
    {
        if (backend == "sqlite")
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                connectionString, @"Data Source\s*=\s*([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && File.Exists(match.Groups[1].Value.Trim());
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
            return System.Text.RegularExpressions.Regex.Replace(
                connectionString, @"(Data Source\s*=\s*)[^;]+",
                $"$1{newDatabase}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        var result = System.Text.RegularExpressions.Regex.Replace(
            connectionString, @"(Database\s*=\s*)[^;]+",
            $"$1{newDatabase}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return System.Text.RegularExpressions.Regex.Replace(
            result, @"(Initial Catalog\s*=\s*)[^;]+",
            $"$1{newDatabase}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public string MaskPassword(string connStr) => _storage.MaskConnectionString(connStr);
}
