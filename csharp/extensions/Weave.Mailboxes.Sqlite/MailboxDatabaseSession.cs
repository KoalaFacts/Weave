using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Weave.Mailboxes.Sqlite;

internal sealed class MailboxDatabaseSession : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly CancellationToken _ct;

    public MailboxDatabaseSession(SqliteConnection connection, bool write, CancellationToken ct)
    {
        _connection = connection;
        _ct = ct;
        _transaction = connection.BeginTransaction(deferred: !write);
    }

    public int Execute(string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, values);
        return command.ExecuteNonQuery();
    }

    public long Scalar(string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, values);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, values);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            _ct.ThrowIfCancellationRequested();
            result.Add(map(reader));
        }
        return result;
    }

    public void Commit()
    {
        _ct.ThrowIfCancellationRequested();
        _transaction.Commit();
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] values)
    {
        _ct.ThrowIfCancellationRequested();
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose()
    {
        _transaction.Dispose();
        _connection.Dispose();
    }
}
