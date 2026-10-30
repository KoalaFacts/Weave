namespace Weave.Security.Tokens;

public sealed record CapabilityTokenRequest
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string IssuedTo { get; init; } = string.Empty;
    public PrincipalKind Kind { get; init; } = PrincipalKind.Agent;
    public HashSet<string> Grants { get; init; } = [];
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(24);
}
