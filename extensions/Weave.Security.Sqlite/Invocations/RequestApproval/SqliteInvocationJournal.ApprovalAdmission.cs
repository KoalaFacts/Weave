using Microsoft.Data.Sqlite;
using Weave.Invocations;

namespace Weave.Security.Sqlite;

public sealed partial class SqliteInvocationJournal
{
    private InvocationClaim? AdmitApproval(SqliteConnection connection, SqliteTransaction transaction,
        InvocationRecord candidate)
    {
        var approval = ReadApproval(connection, transaction, candidate.WorkspaceId, candidate.InvocationId);
        if (approval is null)
        {
            if (!_approvalPolicy.RequiresApproval(candidate.AuthorizedGrant))
                return null;
            if (string.IsNullOrWhiteSpace(candidate.ApprovalTargetDigest))
                return new InvocationClaim(false, candidate) { BlockingReason = "approval-target-unsupported" };
            approval = InvocationApprovalPlan.Create(candidate, _approvalPolicy.Lifetime);
            InsertApproval(connection, transaction, approval, candidate);
        }
        else if (!approval.Matches(candidate))
        {
            return new InvocationClaim(false, candidate) { BlockingReason = "approval-plan-conflict" };
        }

        var current = approval.At(candidate.CreatedAt);
        if (current.State != approval.State)
            SetApprovalState(connection, transaction, approval, current.State);
        approval = current;
        if (approval.State == InvocationApprovalState.Approved)
        {
            SetApprovalState(connection, transaction, approval, InvocationApprovalState.Consumed);
            return new InvocationClaim(true, candidate)
            {
                Approval = approval with { State = InvocationApprovalState.Consumed }
            };
        }

        return new InvocationClaim(false, candidate)
        {
            Approval = approval,
            BlockingReason = approval.State switch
            {
                InvocationApprovalState.Pending => "approval-pending",
                InvocationApprovalState.Rejected => "approval-rejected",
                InvocationApprovalState.Expired => "approval-expired",
                InvocationApprovalState.Cancelled => "approval-cancelled",
                _ => "approval-not-executable"
            }
        };
    }

    private static void SetApprovalState(SqliteConnection connection, SqliteTransaction transaction,
        InvocationApproval approval, InvocationApprovalState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE invocation_approvals SET state=$state
            WHERE workspace_id=$workspace AND invocation_id=$id AND plan_digest=$plan AND state=$previous;
            """;
        AddIdentity(command, approval.WorkspaceId, approval.InvocationId);
        command.Parameters.AddWithValue("$plan", approval.PlanDigest);
        command.Parameters.AddWithValue("$previous", (int)approval.State);
        command.Parameters.AddWithValue("$state", (int)state);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Approval state changed during admission.");
    }
}
