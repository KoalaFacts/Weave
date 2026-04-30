namespace Weave.Security.Tokens;

public sealed record CapabilityToken
{
    public string TokenId { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; init; } = string.Empty;
    public string IssuedTo { get; init; } = string.Empty;
    public HashSet<string> Grants { get; init; } = [];
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string Signature { get; init; } = string.Empty;

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    public bool HasGrant(string grant) =>
        Grants.Contains(grant) || Grants.Contains("*");
}
