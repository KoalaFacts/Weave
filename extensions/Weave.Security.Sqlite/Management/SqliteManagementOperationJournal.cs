using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Management;

namespace Weave.Security.Sqlite;

public sealed class SqliteManagementOperationJournal : IManagementOperationJournal
{
    private readonly string _connectionString;

    public SqliteManagementOperationJournal(IOptions<ManagementJournalOptions> options,
        IOptions<InvocationJournalOptions> invocationOptions)
    {
        var invocationDirectory = invocationOptions.Value.DatabasePath is { } invocationPath
            ? Path.GetDirectoryName(Path.GetFullPath(invocationPath))!
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        var path = options.Value.DatabasePath ?? Path.Join(invocationDirectory, "management.db");
        if (string.IsNullOrWhiteSpace(path) || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Management recording requires an on-disk database path.");
        path = Path.GetFullPath(path);
        if (!options.Value.RequireExistingStorage)
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var initial = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = options.Value.RequireExistingStorage ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        initial.Open();
        using (var mode = initial.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode=WAL;";
            if (!string.Equals(mode.ExecuteScalar()?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The management journal requires SQLite WAL mode.");
        }

        if (options.Value.RequireExistingStorage)
        {
            using var verify = initial.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='management_operations';";
            if (Convert.ToInt32(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The existing management journal has no operation table.");
        }
        else
        {
            using var schema = initial.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS management_operations (
                    id TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    token_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    target TEXT NOT NULL,
                    authorized_grants TEXT NOT NULL,
                    request_digest TEXT NOT NULL,
                    admitted_at TEXT NOT NULL,
                    outcome INTEGER NOT NULL CHECK(outcome BETWEEN 0 AND 2),
                    completed_at TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_management_operations_workspace
                    ON management_operations(workspace_id, admitted_at);
                """;
            schema.ExecuteNonQuery();
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public bool TryAdmit(ManagementOperationRecord operation, CancellationToken cancellationToken)
    {
        if (operation.Outcome is not ManagementOperationOutcome.OutcomeUnknown || operation.CompletedAt is not null)
            throw new ArgumentException("A new management operation must have an unknown outcome.", nameof(operation));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO management_operations(id, workspace_id, subject, token_id, action, target,
                authorized_grants, request_digest, admitted_at, outcome)
            VALUES($id, $workspace, $subject, $token, $action, $target, $grants, $digest, $admitted, 0)
            ON CONFLICT(id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$id", operation.Id);
        insert.Parameters.AddWithValue("$workspace", operation.WorkspaceId);
        insert.Parameters.AddWithValue("$subject", operation.Subject);
        insert.Parameters.AddWithValue("$token", operation.TokenId);
        insert.Parameters.AddWithValue("$action", operation.Action);
        insert.Parameters.AddWithValue("$target", operation.Target);
        insert.Parameters.AddWithValue("$grants", operation.AuthorizedGrants);
        insert.Parameters.AddWithValue("$digest", operation.RequestDigest);
        insert.Parameters.AddWithValue("$admitted", operation.AdmittedAt.ToString("O", CultureInfo.InvariantCulture));
        var inserted = insert.ExecuteNonQuery() == 1;
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return inserted;
    }

    public bool Complete(string id, ManagementOperationOutcome outcome, DateTimeOffset completedAt)
    {
        if (outcome is ManagementOperationOutcome.OutcomeUnknown || !Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        using var connection = Open();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE management_operations SET outcome=$outcome, completed_at=$completed
            WHERE id=$id AND outcome=0 AND completed_at IS NULL;
            """;
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$outcome", (int)outcome);
        update.Parameters.AddWithValue("$completed", completedAt.ToString("O", CultureInfo.InvariantCulture));
        return update.ExecuteNonQuery() == 1;
    }

    public ManagementOperationRecord? Find(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT workspace_id, subject, token_id, action, target, authorized_grants,
                request_digest, admitted_at, outcome, completed_at
            FROM management_operations WHERE id=$id;
            """;
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ManagementOperationRecord
        {
            Id = id,
            WorkspaceId = reader.GetString(0),
            Subject = reader.GetString(1),
            TokenId = reader.GetString(2),
            Action = reader.GetString(3),
            Target = reader.GetString(4),
            AuthorizedGrants = reader.GetString(5),
            RequestDigest = reader.GetString(6),
            AdmittedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            Outcome = (ManagementOperationOutcome)reader.GetInt32(8),
            CompletedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)
        };
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
