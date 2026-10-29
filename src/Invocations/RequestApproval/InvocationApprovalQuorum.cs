namespace Weave.Invocations;

/// <summary>
/// Host-configured number of distinct human approvers an approval-required operation needs.
/// Configured as a list entry because configuration keys cannot contain the ':' in grant scopes.
/// </summary>
public sealed class InvocationApprovalQuorum
{
    public const int MaxRequiredApprovals = 3;

    /// <summary>An exact entry from <see cref="InvocationJournalOptions.ApprovalRequiredGrants"/>.</summary>
    public string Grant { get; set; } = string.Empty;

    /// <summary>Distinct human approvers, excluding the requester; 1 to <see cref="MaxRequiredApprovals"/>.</summary>
    public int RequiredApprovals { get; set; } = 1;
}
