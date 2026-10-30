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
        if (decision == InvocationApprovalDecision.Approve)
            return Approve(connection, transaction, current, decider, tokenId, now, cancellationToken);
        var nextState = decision == InvocationApprovalDecision.Reject
            ? InvocationApprovalState.Rejected
            : InvocationApprovalState.Cancelled;
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

    // Each distinct human approval is recorded; the plan becomes executable only at its stored quorum.
    private static InvocationApprovalDecisionResult Approve(SqliteConnection connection, SqliteTransaction transaction,
        InvocationApproval current, string decider, string tokenId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var alreadyApproved = HasApproved(connection, transaction, current, decider);
        if (alreadyApproved && current.State is InvocationApprovalState.Pending or InvocationApprovalState.Approved)
            return new(current, null);
        if (current.State != InvocationApprovalState.Pending)
            return new(current, "approval-already-decided");

        var approvals = current.Approvals + 1;
        var nextState = approvals >= current.RequiredApprovals
            ? InvocationApprovalState.Approved
            : InvocationApprovalState.Pending;
        PersistApprovalDecision(connection, transaction, current, nextState, InvocationApprovalDecision.Approve,
            decider, tokenId, now);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        var updated = current with { Approvals = approvals };
        return new(nextState == InvocationApprovalState.Approved
            ? updated with { State = nextState, DecidedBy = decider, DecisionTokenId = tokenId, DecidedAt = now }
            : updated, null);
    }

    private static bool HasApproved(SqliteConnection connection, SqliteTransaction transaction,
        InvocationApproval approval, string decider)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM invocation_approval_decisions
                WHERE workspace_id=$workspace AND invocation_id=$id AND plan_digest=$plan
                    AND decision=$approve AND decider=$decider);
            """;
        AddIdentity(command, approval.WorkspaceId, approval.InvocationId);
        command.Parameters.AddWithValue("$plan", approval.PlanDigest);
        command.Parameters.AddWithValue("$approve", (int)InvocationApprovalDecision.Approve);
        command.Parameters.AddWithValue("$decider", decider);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void PersistApprovalDecision(SqliteConnection connection, SqliteTransaction transaction,
        InvocationApproval approval, InvocationApprovalState state, InvocationApprovalDecision decision,
        string decider, string tokenId, DateTimeOffset now)
    {
        SetApprovalState(connection, transaction, approval, state);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Partial quorum approvals leave the final-decision columns untouched; history keeps every vote.
        var final = state != approval.State ? """
            UPDATE invocation_approvals SET decided_by=$decider, decision_token_id=$token, decided_at=$now
            WHERE workspace_id=$workspace AND invocation_id=$id AND plan_digest=$plan;
            """ : string.Empty;
        command.CommandText = final + """
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
