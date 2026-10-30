using Microsoft.Extensions.Options;
using Npgsql;
using Weave.Security.Audit;
using Weave.Security.Events;

namespace Weave.Security.Postgres;

/// <summary>
/// PostgreSQL-backed <see cref="ICapabilityAuditStore"/> for multi-silo
/// deployments where every silo writes into one shared audit log.
/// </summary>
/// <remarks>
/// Mirrors <c>SqliteCapabilityAuditStore</c>: same schema shape, same FIFO
/// trim-on-insert, same query semantics. Connections are taken from Npgsql's
/// built-in pool per operation rather than held open — matches the idiomatic
/// ADO.NET pattern and keeps the store stateless after construction.
/// </remarks>
public sealed class PostgresCapabilityAuditStore : ICapabilityAuditStore
{
    private readonly string _connectionString;
    private readonly int _capacity;

    public PostgresCapabilityAuditStore(IOptions<CapabilityAuditOptions> options)
    {
        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options));
        if (resolved.Capacity <= 0)
            throw new InvalidOperationException(
                $"CapabilityAudit:Capacity must be greater than zero. Current value: {resolved.Capacity}.");
        if (string.IsNullOrWhiteSpace(resolved.ConnectionString))
            throw new InvalidOperationException(
                "CapabilityAudit:ConnectionString is required when Backend is 'postgresql'.");

        _capacity = resolved.Capacity;
        _connectionString = resolved.ConnectionString;
        EnsureSchema();
    }

    public void Record(CapabilityAuthorizationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO capability_audit
                    (timestamp, token_id, grant_value, issued_to, workspace_id, action_context, outcome, reason)
                VALUES
                    (@timestamp, @token_id, @grant, @issued_to, @workspace_id, @action_context, @outcome, @reason);
                """;
            insert.Parameters.AddWithValue("@timestamp", @event.Timestamp.UtcDateTime);
            insert.Parameters.AddWithValue("@token_id", @event.TokenId);
            insert.Parameters.AddWithValue("@grant", @event.Grant);
            insert.Parameters.AddWithValue("@issued_to", @event.IssuedTo);
            insert.Parameters.AddWithValue("@workspace_id", @event.WorkspaceId);
            insert.Parameters.AddWithValue("@action_context", @event.ActionContext);
            insert.Parameters.AddWithValue("@outcome", (int)@event.Outcome);
            insert.Parameters.AddWithValue("@reason", (object?)@event.Reason ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        // FIFO eviction beyond capacity. Same trim shape as SQLite — single
        // statement so the trim is atomic with the insert under the
        // surrounding transaction. ORDER BY row_id DESC + OFFSET @capacity
        // skips the @capacity newest rows and returns the older overflow,
        // which is what we want to delete.
        using (var trim = connection.CreateCommand())
        {
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM capability_audit
                WHERE row_id IN (
                    SELECT row_id FROM capability_audit
                    ORDER BY row_id DESC
                    OFFSET @capacity
                );
                """;
            trim.Parameters.AddWithValue("@capacity", _capacity);
            trim.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<CapabilityAuthorizationEvent> GetByToken(string tokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, token_id, grant_value, issued_to, workspace_id, action_context, outcome, reason
            FROM capability_audit
            WHERE token_id = @token_id
            ORDER BY row_id ASC;
            """;
        cmd.Parameters.AddWithValue("@token_id", tokenId);
        return ReadAll(cmd);
    }

    public IReadOnlyList<CapabilityAuthorizationEvent> GetRecent(int limit)
    {
        if (limit <= 0)
            return [];

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, token_id, grant_value, issued_to, workspace_id, action_context, outcome, reason
            FROM capability_audit
            ORDER BY row_id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadAll(cmd);
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS capability_audit (
                row_id         BIGSERIAL PRIMARY KEY,
                timestamp      TIMESTAMPTZ NOT NULL,
                token_id       TEXT NOT NULL,
                grant_value    TEXT NOT NULL,
                issued_to      TEXT NOT NULL,
                workspace_id   TEXT NOT NULL,
                action_context TEXT NOT NULL,
                outcome        INTEGER NOT NULL,
                reason         TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_capability_audit_token
                ON capability_audit(token_id, row_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private static List<CapabilityAuthorizationEvent> ReadAll(NpgsqlCommand cmd)
    {
        var rows = new List<CapabilityAuthorizationEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var timestampUtc = reader.GetDateTime(0);
            var tokenId = reader.GetString(1);
            var workspaceId = reader.GetString(4);

            rows.Add(new CapabilityAuthorizationEvent
            {
                // SourceId / EventId are not persisted (matching the SQLite
                // backend); SourceId is reconstructable, EventId is
                // regenerated on read and not surfaced through the API.
                SourceId = $"{workspaceId}/{tokenId}",
                Timestamp = new DateTimeOffset(DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc)),
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
}
