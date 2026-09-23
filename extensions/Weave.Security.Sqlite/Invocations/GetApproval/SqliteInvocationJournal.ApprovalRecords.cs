using Microsoft.Data.Sqlite;
using Weave.Invocations;
using Weave.Shared.Ids;

namespace Weave.Security.Sqlite;

public sealed partial class SqliteInvocationJournal
{
    private const string ApprovalSchema = """
        CREATE TABLE IF NOT EXISTS invocation_approvals (
            workspace_id TEXT NOT NULL, invocation_id TEXT NOT NULL,
            subject TEXT NOT NULL, tool_name TEXT NOT NULL, operation TEXT NOT NULL,
            input_digest TEXT NOT NULL, target_digest TEXT NOT NULL, plan_digest TEXT NOT NULL,
            request_token_id TEXT NOT NULL, authorized_grant TEXT NOT NULL,
            requested_at TEXT NOT NULL, expires_at TEXT NOT NULL,
            state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 5),
            decided_by TEXT, decision_token_id TEXT, decided_at TEXT,
            PRIMARY KEY(workspace_id, invocation_id)
        );
        CREATE TABLE IF NOT EXISTS invocation_approval_decisions (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL, invocation_id TEXT NOT NULL, plan_digest TEXT NOT NULL,
            decision INTEGER NOT NULL, previous_state INTEGER NOT NULL, next_state INTEGER NOT NULL,
            decider TEXT NOT NULL, token_id TEXT NOT NULL, decided_at TEXT NOT NULL,
            FOREIGN KEY(workspace_id, invocation_id) REFERENCES invocation_approvals(workspace_id, invocation_id)
        );
        """;

    public InvocationApproval? FindApproval(string workspaceId, InvocationId invocationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        var result = ReadApproval(connection, null, workspaceId, invocationId);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static InvocationApproval? ReadApproval(SqliteConnection connection, SqliteTransaction? transaction,
        string workspaceId, InvocationId invocationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT invocation_id, workspace_id, subject, tool_name, operation,
                input_digest, target_digest, plan_digest, requested_at, expires_at, state,
                decided_by, decision_token_id, decided_at
            FROM invocation_approvals WHERE workspace_id=$workspace AND invocation_id=$id;
            """;
        AddIdentity(command, workspaceId, invocationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new InvocationApproval(InvocationId.From(reader.GetString(0)), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8)), Parse(reader.GetString(9)),
            (InvocationApprovalState)reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : Parse(reader.GetString(13)));
    }

    private static void InsertApproval(SqliteConnection connection, SqliteTransaction transaction,
        InvocationApproval approval, InvocationRecord candidate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO invocation_approvals(workspace_id, invocation_id, subject, tool_name, operation,
                input_digest, target_digest, plan_digest, request_token_id, authorized_grant,
                requested_at, expires_at, state)
            VALUES($workspace, $id, $subject, $tool, $operation, $input, $target, $plan, $token, $grant,
                $requested, $expires, 0);
            """;
        AddIdentity(command, approval.WorkspaceId, approval.InvocationId);
        command.Parameters.AddWithValue("$subject", approval.Subject);
        command.Parameters.AddWithValue("$tool", approval.ToolName);
        command.Parameters.AddWithValue("$operation", approval.Operation);
        command.Parameters.AddWithValue("$input", approval.InputDigest);
        command.Parameters.AddWithValue("$target", approval.TargetDigest);
        command.Parameters.AddWithValue("$plan", approval.PlanDigest);
        command.Parameters.AddWithValue("$token", candidate.TokenId);
        command.Parameters.AddWithValue("$grant", candidate.AuthorizedGrant);
        command.Parameters.AddWithValue("$requested", Format(approval.RequestedAt));
        command.Parameters.AddWithValue("$expires", Format(approval.ExpiresAt));
        command.ExecuteNonQuery();
    }
}
