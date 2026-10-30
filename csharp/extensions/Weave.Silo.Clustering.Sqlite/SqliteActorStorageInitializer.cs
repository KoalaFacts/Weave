using Microsoft.Data.Sqlite;

namespace Weave.Silo.Clustering.Sqlite;

internal static class SqliteActorStorageInitializer
{
    public static void Initialize(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode is SqliteOpenMode.Memory || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite actor storage requires a file-backed database.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var hasQuery = TableExists(connection, "OrleansQuery");
        var hasStorage = TableExists(connection, "OrleansStorage");
        if (hasQuery && hasStorage)
            return;
        if (hasQuery || hasStorage)
            throw new InvalidOperationException("SQLite actor storage has an incomplete Orleans schema.");

        using var transaction = connection.BeginTransaction();
        ExecuteResource(connection, transaction, "Sqlite-Main.sql");
        ExecuteResource(connection, transaction, "Sqlite-Persistence.sql");
        transaction.Commit();
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static void ExecuteResource(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var stream = typeof(SqliteActorStorageInitializer).Assembly.GetManifestResourceStream(
            $"Weave.Silo.Clustering.Sqlite.{name}")
            ?? throw new InvalidOperationException($"SQLite actor storage schema resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
    }
}
