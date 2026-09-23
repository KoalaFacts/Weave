using Microsoft.Data.Sqlite;
using Weave.Invocations;
using Weave.Shared.Ids;

namespace Weave.Security.Sqlite;

public sealed partial class SqliteInvocationJournal
{
    public InvocationApprovalDecisionResult DecideApproval(string workspaceId, InvocationId invocationId,
        string planDigest, string decider, string tokenId, InvocationApprovalDecision decision,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(decision) || string.IsNullOrWhiteSpace(decider) || string.IsNullOrWhiteSpace(tokenId))
            return new(null, "invalid-approval-decision");
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadApproval(connection, transaction, workspaceId, invocationId);
        if (stored is null)
            return new(null, "approval-not-found");
        if (!string.Equals(stored.PlanDigest, planDigest, StringComparison.Ordinal))
            return new(null, "approval-plan-conflict");
        if (decision == InvocationApprovalDecision.Cancel ? decider != stored.Subject : decider == stored.Subject)
            return new(null, "approval-subject-denied");

        var current = stored.At(now);
        if (current.State != stored.State)
        {
            SetApprovalState(connection, transaction, stored, current.State);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(current, "approval-expired");
        }
        var nextState = decision switch
        {
            InvocationApprovalDecision.Approve => InvocationApprovalState.Approved,
            InvocationApprovalDecision.Reject => InvocationApprovalState.Rejected,
            _ => InvocationApprovalState.Cancelled
        };
        if (current.State == nextState && current.DecidedBy == decider)
            return new(current, null);
        var mayDecide = current.State == InvocationApprovalState.Pending
            || decision == InvocationApprovalDecision.Cancel && current.State == InvocationApprovalState.Approved;
        if (!mayDecide)
            return new(current, "approval-already-decided");

        PersistApprovalDecision(connection, transaction, current, nextState, decision, decider, tokenId, now);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(current with { State = nextState, DecidedBy = decider, DecisionTokenId = tokenId, DecidedAt = now }, null);
    }

    private static void PersistApprovalDecision(SqliteConnection connection, SqliteTransaction transaction,
        InvocationApproval approval, InvocationApprovalState state, InvocationApprovalDecision decision,
        string decider, string tokenId, DateTimeOffset now)
    {
        SetApprovalState(connection, transaction, approval, state);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE invocation_approvals SET decided_by=$decider, decision_token_id=$token, decided_at=$now
            WHERE workspace_id=$workspace AND invocation_id=$id AND plan_digest=$plan;
            INSERT INTO invocation_approval_decisions(workspace_id, invocation_id, plan_digest,
                decision, previous_state, next_state, decider, token_id, decided_at)
            VALUES($workspace, $id, $plan, $decision, $previous, $state, $decider, $token, $now);
            """;
        AddIdentity(command, approval.WorkspaceId, approval.InvocationId);
        command.Parameters.AddWithValue("$plan", approval.PlanDigest);
        command.Parameters.AddWithValue("$decision", (int)decision);
        command.Parameters.AddWithValue("$previous", (int)approval.State);
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$decider", decider);
        command.Parameters.AddWithValue("$token", tokenId);
        command.Parameters.AddWithValue("$now", Format(now));
        command.ExecuteNonQuery();
    }
}
