using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Weave.Invocations;
using Weave.Shared.Ids;

namespace Weave.Security.Sqlite;

/// <summary>On-disk journal. Short SQLite transactions claim one attempt; none span dispatch.</summary>
public sealed class SqliteInvocationJournal : IInvocationJournal
{
    private readonly string _connectionString;

    public SqliteInvocationJournal(IOptions<InvocationJournalOptions> options)
    {
        var path = options.Value.DatabasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "invocations.db");
        if (string.IsNullOrWhiteSpace(path) || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invocation recording requires an unambiguous on-disk database path.");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode=WAL;";
        if (!string.Equals(mode.ExecuteScalar()?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The invocation journal requires SQLite WAL mode.");
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS invocations (
                workspace_id TEXT NOT NULL, invocation_id TEXT NOT NULL,
                subject TEXT NOT NULL, tool_name TEXT NOT NULL, operation TEXT NOT NULL,
                input_digest TEXT NOT NULL, token_id TEXT NOT NULL, authorized_grant TEXT NOT NULL,
                created_at TEXT NOT NULL, PRIMARY KEY(workspace_id, invocation_id)
            );
            CREATE TABLE IF NOT EXISTS invocation_attempts (
                workspace_id TEXT NOT NULL, invocation_id TEXT NOT NULL, attempt_id TEXT NOT NULL UNIQUE,
                started_at TEXT NOT NULL, outcome INTEGER NOT NULL CHECK(outcome BETWEEN 0 AND 5),
                completed_at TEXT, duration_ticks INTEGER NOT NULL CHECK(duration_ticks >= 0),
                PRIMARY KEY(workspace_id, invocation_id),
                FOREIGN KEY(workspace_id, invocation_id) REFERENCES invocations(workspace_id, invocation_id)
            );
            """;
        schema.ExecuteNonQuery();
    }

    public InvocationClaim TryStart(InvocationRecord candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Attempt.Outcome != InvocationOutcome.OutcomeUnknown || candidate.Attempt.CompletedAt is not null)
            throw new ArgumentException("A new attempt must have an unconfirmed outcome.", nameof(candidate));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO invocations(workspace_id, invocation_id, subject, tool_name, operation,
                input_digest, token_id, authorized_grant, created_at)
            VALUES($workspace, $id, $subject, $tool, $operation, $digest, $token, $grant, $created)
            ON CONFLICT(workspace_id, invocation_id) DO NOTHING;
            """;
        AddIdentity(insert, candidate.WorkspaceId, candidate.InvocationId);
        insert.Parameters.AddWithValue("$subject", candidate.Subject);
        insert.Parameters.AddWithValue("$tool", candidate.ToolName);
        insert.Parameters.AddWithValue("$operation", candidate.Operation);
        insert.Parameters.AddWithValue("$digest", candidate.InputDigest);
        insert.Parameters.AddWithValue("$token", candidate.TokenId);
        insert.Parameters.AddWithValue("$grant", candidate.AuthorizedGrant);
        insert.Parameters.AddWithValue("$created", Format(candidate.CreatedAt));
        if (insert.ExecuteNonQuery() == 0)
        {
            var existing = Read(connection, transaction, candidate.WorkspaceId, candidate.InvocationId)
                ?? throw new InvalidOperationException("Invocation intent has no corresponding attempt.");
            return new InvocationClaim(false, existing);
        }
        using var attempt = connection.CreateCommand();
        attempt.Transaction = transaction;
        attempt.CommandText = """
            INSERT INTO invocation_attempts(workspace_id, invocation_id, attempt_id,
                started_at, outcome, completed_at, duration_ticks)
            VALUES($workspace, $id, $attempt, $started, 0, NULL, 0);
            """;
        AddIdentity(attempt, candidate.WorkspaceId, candidate.InvocationId);
        attempt.Parameters.AddWithValue("$attempt", candidate.Attempt.AttemptId.ToString());
        attempt.Parameters.AddWithValue("$started", Format(candidate.Attempt.StartedAt));
        attempt.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new InvocationClaim(true, candidate);
    }

    public InvocationRecord? Find(string workspaceId, InvocationId invocationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        return Read(connection, null, workspaceId, invocationId);
    }

    public bool Complete(string workspaceId, InvocationId invocationId, InvocationAttemptId attemptId,
        InvocationOutcome outcome, DateTimeOffset completedAt, TimeSpan duration)
    {
        if (!Enum.IsDefined(outcome) || duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE invocation_attempts SET outcome=$outcome, completed_at=$completed, duration_ticks=$duration
            WHERE workspace_id=$workspace AND invocation_id=$id AND attempt_id=$attempt AND completed_at IS NULL;
            """;
        AddIdentity(command, workspaceId, invocationId);
        command.Parameters.AddWithValue("$attempt", attemptId.ToString());
        command.Parameters.AddWithValue("$outcome", (int)outcome);
        command.Parameters.AddWithValue("$completed", Format(completedAt));
        command.Parameters.AddWithValue("$duration", duration.Ticks);
        return command.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static InvocationRecord? Read(SqliteConnection connection, SqliteTransaction? transaction,
        string workspaceId, InvocationId invocationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.invocation_id, i.workspace_id, i.subject, i.tool_name, i.operation,
                i.input_digest, i.token_id, i.authorized_grant, i.created_at,
                a.attempt_id, a.started_at, a.outcome, a.completed_at, a.duration_ticks
            FROM invocations i JOIN invocation_attempts a
                ON i.workspace_id=a.workspace_id AND i.invocation_id=a.invocation_id
            WHERE i.workspace_id=$workspace AND i.invocation_id=$id;
            """;
        AddIdentity(command, workspaceId, invocationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new InvocationRecord(InvocationId.From(reader.GetString(0)), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8)),
            new InvocationAttempt(InvocationAttemptId.From(reader.GetString(9)), Parse(reader.GetString(10)),
                (InvocationOutcome)reader.GetInt32(11), reader.IsDBNull(12) ? null : Parse(reader.GetString(12)),
                TimeSpan.FromTicks(reader.GetInt64(13))));
    }

    private static void AddIdentity(SqliteCommand command, string workspaceId, InvocationId invocationId)
    {
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$id", invocationId.ToString());
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
