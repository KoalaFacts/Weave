using Microsoft.Data.Sqlite;
using Weave.Invocations;

namespace Weave.Security.Sqlite;

public sealed partial class SqliteInvocationJournal : IInvocationApprovalStore
{
    private static void InitializeApprovals(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS invocation_approvals (
                workspace_id TEXT NOT NULL, invocation_id TEXT NOT NULL,
                subject TEXT NOT NULL, tool_name TEXT NOT NULL, input_digest TEXT NOT NULL,
                plan_digest TEXT NOT NULL, protected_plan TEXT NOT NULL, created_at TEXT NOT NULL,
                expires_ticks INTEGER NOT NULL, state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 5),
                decided_by TEXT, decision_token_id TEXT, decided_at TEXT,
                cancelled_by TEXT, cancelled_at TEXT,
                PRIMARY KEY(workspace_id, invocation_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    public ApprovalRecord? ProposeApproval(ApprovalRecord candidate, CancellationToken cancellationToken)
    {
        if (candidate.State != ApprovalState.Pending || candidate.DecidedBy is not null)
            throw new ArgumentException("A proposed approval must be pending.", nameof(candidate));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ExpireApproval(connection, transaction, candidate.WorkspaceId, candidate.InvocationId);
        var existing = ReadApproval(connection, transaction, candidate.WorkspaceId, candidate.InvocationId);
        if (existing is not null)
        {
            transaction.Commit();
            return existing;
        }
        if (Read(connection, transaction, candidate.WorkspaceId, candidate.InvocationId) is not null)
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO invocation_approvals(workspace_id, invocation_id, subject, tool_name,
                input_digest, plan_digest, protected_plan, created_at, expires_ticks, state)
            VALUES($workspace, $id, $subject, $tool, $input, $digest, $plan, $created, $expires, 0);
            """;
        AddIdentity(command, candidate.WorkspaceId, candidate.InvocationId);
        command.Parameters.AddWithValue("$subject", candidate.Subject);
        command.Parameters.AddWithValue("$tool", candidate.ToolName);
        command.Parameters.AddWithValue("$input", candidate.InputDigest);
        command.Parameters.AddWithValue("$digest", candidate.PlanDigest);
        command.Parameters.AddWithValue("$plan", candidate.ProtectedPlan);
        command.Parameters.AddWithValue("$created", Format(candidate.CreatedAt));
        command.Parameters.AddWithValue("$expires", candidate.ExpiresAt.UtcTicks);
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return candidate;
    }

    public ApprovalRecord? FindApproval(string workspaceId, InvocationId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ExpireApproval(connection, transaction, workspaceId, id);
        var record = ReadApproval(connection, transaction, workspaceId, id);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return record;
    }

    public bool TryDecideApproval(string workspaceId, InvocationId id, string digest, ApprovalDecision decision,
        string subject, string tokenId, DateTimeOffset tokenExpiry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(decision))
            return false;
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = decision == ApprovalDecision.Cancel ? """
            UPDATE invocation_approvals SET state=4, cancelled_by=$subject, cancelled_at=$now
            WHERE workspace_id=$workspace AND invocation_id=$id AND plan_digest=$digest
                AND state IN (0, 1) AND subject=$subject AND expires_ticks>$ticks;
            """ : """
            UPDATE invocation_approvals SET state=$state, decided_by=$subject,
                decision_token_id=$token, decided_at=$now, expires_ticks=MIN(expires_ticks, $tokenExpiry)
            WHERE workspace_id=$workspace AND invocation_id=$id AND plan_digest=$digest
                AND state=0 AND subject<>$subject AND expires_ticks>$ticks AND $tokenExpiry>$ticks;
            """;
        AddIdentity(command, workspaceId, id);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$ticks", now.UtcTicks);
        if (decision != ApprovalDecision.Cancel)
        {
            command.Parameters.AddWithValue("$state", decision == ApprovalDecision.Approve ? 1 : 2);
            command.Parameters.AddWithValue("$token", tokenId);
            command.Parameters.AddWithValue("$tokenExpiry", tokenExpiry.UtcTicks);
        }
        var changed = command.ExecuteNonQuery() == 1;
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return changed;
    }

    private void ExpireApproval(SqliteConnection connection, SqliteTransaction transaction, string workspace, InvocationId id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE invocation_approvals SET state=3
            WHERE workspace_id=$workspace AND invocation_id=$id AND state IN (0, 1) AND expires_ticks<=$now;
            """;
        AddIdentity(command, workspace, id);
        command.Parameters.AddWithValue("$now", _approvalTime.GetUtcNow().UtcTicks);
        command.ExecuteNonQuery();
    }

    private string? ConsumeApproval(SqliteConnection connection, SqliteTransaction transaction, InvocationRecord candidate)
    {
        var approval = ReadApproval(connection, transaction, candidate.WorkspaceId, candidate.InvocationId);
        if (approval is null)
            return candidate.ApprovalPlanDigest is null ? null : "approval-not-found";
        if (approval.Subject != candidate.Subject || approval.ToolName != candidate.ToolName
            || candidate.Operation != "write_file" || approval.InputDigest != candidate.InputDigest
            || approval.PlanDigest != candidate.ApprovalPlanDigest)
            return "approval-plan-conflict";
        if (approval.State != ApprovalState.Approved || approval.ExpiresAt <= _approvalTime.GetUtcNow())
            return "approval-not-ready";
        using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = """
            UPDATE invocation_approvals SET state=5
            WHERE workspace_id=$workspace AND invocation_id=$id AND state=1;
            """;
        AddIdentity(consume, candidate.WorkspaceId, candidate.InvocationId);
        if (consume.ExecuteNonQuery() != 1)
            return "approval-not-ready";
        return null;
    }

    private static ApprovalRecord? ReadApproval(SqliteConnection connection, SqliteTransaction? transaction,
        string workspaceId, InvocationId id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT subject, tool_name, input_digest, plan_digest, protected_plan, created_at,
                expires_ticks, state, decided_by, decision_token_id, decided_at, cancelled_by, cancelled_at
            FROM invocation_approvals WHERE workspace_id=$workspace AND invocation_id=$id;
            """;
        AddIdentity(command, workspaceId, id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ApprovalRecord(id, workspaceId, reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), Parse(reader.GetString(5)),
            new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero), (ApprovalState)reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : Parse(reader.GetString(12)));
    }
}
