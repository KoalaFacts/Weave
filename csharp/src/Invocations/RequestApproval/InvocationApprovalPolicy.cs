using Weave.Shared.Capabilities;

namespace Weave.Invocations;

/// <summary>Immutable host-configured requirements; approval adds no invocation grants.</summary>
public sealed class InvocationApprovalPolicy
{
    private readonly string[] _grants;
    private readonly (string Grant, int RequiredApprovals)[] _quorums;
    public TimeSpan Lifetime { get; }

    public InvocationApprovalPolicy(InvocationJournalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ApprovalRequiredGrants);
        _grants = [.. options.ApprovalRequiredGrants];
        Lifetime = options.ApprovalLifetime;
        if (Lifetime < TimeSpan.FromSeconds(1) || Lifetime > TimeSpan.FromDays(7))
            throw new ArgumentException("Approval lifetime must be between one second and seven days.", nameof(options));
        foreach (var grant in _grants)
        {
            var parts = grant?.Split(':');
            if (grant is null || parts is not { Length: 4 } || parts[0] != "tool" || parts[2] != "invoke"
                || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[3])
                || !string.Equals(grant, grant.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("Approval requirements must use tool:<name>:invoke:<operation> scopes.", nameof(options));
        }
        _quorums = SnapshotQuorums(options.ApprovalQuorums, _grants);
    }

    public bool RequiresApproval(string operationGrant) => CapabilityGrantMatcher.HasGrant(_grants, operationGrant);

    /// <summary>The strictest configured quorum matching the operation; one when none is configured.</summary>
    public int RequiredApprovals(string operationGrant)
    {
        var required = 1;
        foreach (var (grant, approvals) in _quorums)
        {
            if (CapabilityGrantMatcher.HasGrant([grant], operationGrant))
                required = Math.Max(required, approvals);
        }
        return required;
    }

    private static (string, int)[] SnapshotQuorums(List<InvocationApprovalQuorum>? quorums, string[] grants)
    {
        ArgumentNullException.ThrowIfNull(quorums);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new List<(string, int)>(quorums.Count);
        foreach (var quorum in quorums)
        {
            if (quorum is null || !grants.Contains(quorum.Grant, StringComparer.Ordinal) || !seen.Add(quorum.Grant))
                throw new ArgumentException("Approval quorums must name a distinct configured approval requirement.", nameof(quorums));
            if (quorum.RequiredApprovals is < 1 or > InvocationApprovalQuorum.MaxRequiredApprovals)
                throw new ArgumentException(
                    $"Approval quorums must require between 1 and {InvocationApprovalQuorum.MaxRequiredApprovals} approvers.", nameof(quorums));
            snapshot.Add((quorum.Grant, quorum.RequiredApprovals));
        }
        return [.. snapshot];
    }
}
