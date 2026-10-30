using Microsoft.Data.Sqlite;

namespace Weave.Security.Sqlite;

public sealed partial class SqliteInvocationJournal
{
    // Probe every current column without changing or repairing retained evidence.
    private static void ValidateRetainedSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id, invocation_id, subject, tool_name, operation, input_digest,
                token_id, authorized_grant, created_at FROM invocations LIMIT 0;
            SELECT workspace_id, invocation_id, attempt_id, started_at, outcome,
                completed_at, duration_ticks FROM invocation_attempts LIMIT 0;
            SELECT workspace_id, invocation_id, subject, tool_name, operation, input_digest,
                target_digest, plan_digest, request_token_id, authorized_grant, requested_at,
                expires_at, state, decided_by, decision_token_id, decided_at
                FROM invocation_approvals LIMIT 0;
            SELECT sequence, workspace_id, invocation_id, plan_digest, decision,
                previous_state, next_state, decider, token_id, decided_at
                FROM invocation_approval_decisions LIMIT 0;
            """;
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
            // Execute each zero-row probe, including the approval history table.
        }
    }
}
