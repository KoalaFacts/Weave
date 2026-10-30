using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Weave.Security.Audit;
using Weave.Security.Events;

namespace Weave.Security.Sqlite;

/// <summary>
/// SQLite-backed <see cref="ICapabilityAuditStore"/>. Survives silo restart
/// and process recycle, unlike <c>InMemoryCapabilityAuditStore</c>.
/// One shared connection guarded by a single lock — write/query rates for
/// an audit log stay well below contention thresholds, and SQLite's own
/// default journaling handles durability.
/// </summary>
public sealed class SqliteCapabilityAuditStore : ICapabilityAuditStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private bool _disposed;

    public SqliteCapabilityAuditStore(IOptions<CapabilityAuditOptions> options)
    {
        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        if (resolved.Capacity <= 0)
            throw new InvalidOperationException(
                $"CapabilityAudit:Capacity must be greater than zero. Current value: {resolved.Capacity}.");
        _capacity = resolved.Capacity;

        var connectionString = string.IsNullOrWhiteSpace(resolved.ConnectionString)
            ? DefaultConnectionString()
            : resolved.ConnectionString;
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        EnsureSchema();
    }

    public void Record(CapabilityAuthorizationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        lock (_gate)
        {
            ThrowIfDisposed();

            using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO capability_audit
                    (timestamp, token_id, grant_value, issued_to, workspace_id, action_context, outcome, reason)
                VALUES
                    ($timestamp, $token_id, $grant, $issued_to, $workspace_id, $action_context, $outcome, $reason);
                """;
            insert.Parameters.AddWithValue("$timestamp", @event.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$token_id", @event.TokenId);
            insert.Parameters.AddWithValue("$grant", @event.Grant);
            insert.Parameters.AddWithValue("$issued_to", @event.IssuedTo);
            insert.Parameters.AddWithValue("$workspace_id", @event.WorkspaceId);
            insert.Parameters.AddWithValue("$action_context", @event.ActionContext);
            insert.Parameters.AddWithValue("$outcome", (int)@event.Outcome);
            insert.Parameters.AddWithValue("$reason", (object?)@event.Reason ?? DBNull.Value);
            insert.ExecuteNonQuery();

            // FIFO eviction beyond capacity. Single statement so the trim
            // is atomic with respect to other writers behind the same lock.
            using var trim = _connection.CreateCommand();
            trim.CommandText = """
                DELETE FROM capability_audit
                WHERE rowid IN (
                    SELECT rowid FROM capability_audit
                    ORDER BY rowid ASC
                    LIMIT MAX(0, (SELECT COUNT(*) FROM capability_audit) - $capacity)
                );
                """;
            trim.Parameters.AddWithValue("$capacity", _capacity);
            trim.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<CapabilityAuthorizationEvent> GetByToken(string tokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);

        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, token_id, grant_value, issued_to, workspace_id, action_context, outcome, reason
                FROM capability_audit
                WHERE token_id = $token_id
                ORDER BY rowid ASC;
                """;
            cmd.Parameters.AddWithValue("$token_id", tokenId);
            return ReadAll(cmd);
        }
    }

    public IReadOnlyList<CapabilityAuthorizationEvent> GetRecent(int limit)
    {
        if (limit <= 0)
            return [];

        lock (_gate)
        {
            ThrowIfDisposed();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, token_id, grant_value, issued_to, workspace_id, action_context, outcome, reason
                FROM capability_audit
                ORDER BY rowid DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadAll(cmd);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _connection.Dispose();
            _disposed = true;
        }
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS capability_audit (
                rowid          INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp      TEXT NOT NULL,
                token_id       TEXT NOT NULL,
                grant_value    TEXT NOT NULL,
                issued_to      TEXT NOT NULL,
                workspace_id   TEXT NOT NULL,
                action_context TEXT NOT NULL,
                outcome        INTEGER NOT NULL,
                reason         TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_capability_audit_token
                ON capability_audit(token_id, rowid);
            """;
        cmd.ExecuteNonQuery();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static List<CapabilityAuthorizationEvent> ReadAll(SqliteCommand cmd)
    {
        var rows = new List<CapabilityAuthorizationEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var timestamp = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var tokenId = reader.GetString(1);
            var workspaceId = reader.GetString(4);

            rows.Add(new CapabilityAuthorizationEvent
            {
                // SourceId / EventId / Timestamp are not persisted on the row —
                // SourceId is reconstructable, EventId is regenerated on read
                // (the API surface does not expose it). Timestamp comes from
                // the persisted column.
                SourceId = $"{workspaceId}/{tokenId}",
                Timestamp = timestamp,
                TokenId = tokenId,
                Grant = reader.GetString(2),
                IssuedTo = reader.GetString(3),
                WorkspaceId = workspaceId,
                ActionContext = reader.GetString(5),
                Outcome = (CapabilityAuthorizationOutcome)reader.GetInt32(6),
                Reason = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }
        return rows;
    }

    private static string DefaultConnectionString()
    {
        var weaveHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        Directory.CreateDirectory(weaveHome);
        return $"Data Source={Path.Combine(weaveHome, "audit.db")}";
    }
}
