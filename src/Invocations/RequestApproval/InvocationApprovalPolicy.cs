using Weave.Shared.Capabilities;

namespace Weave.Invocations;

/// <summary>Immutable host-configured requirements; approval adds no invocation grants.</summary>
public sealed class InvocationApprovalPolicy
{
    private readonly string[] _grants;
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
            if (parts is not { Length: 4 } || parts[0] != "tool" || parts[2] != "invoke"
                || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[3])
                || !string.Equals(grant, grant.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("Approval requirements must use tool:<name>:invoke:<operation> scopes.", nameof(options));
        }
    }

    public bool RequiresApproval(string operationGrant) => CapabilityGrantMatcher.HasGrant(_grants, operationGrant);
}
