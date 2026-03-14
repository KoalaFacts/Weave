namespace Weave.Security.Tokens;

[GenerateSerializer]
public sealed record CapabilityToken
{
    [Id(0)] public string TokenId { get; init; } = Guid.NewGuid().ToString("N");
    [Id(1)] public string WorkspaceId { get; init; } = string.Empty;
    [Id(2)] public string IssuedTo { get; init; } = string.Empty;
    [Id(3)] public HashSet<string> Grants { get; init; } = [];
    [Id(4)] public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(5)] public DateTimeOffset ExpiresAt { get; init; }
    [Id(6)] public string Signature { get; init; } = string.Empty;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    public bool HasGrant(string grant) =>
        Grants.Contains(grant) || Grants.Contains("*");
}

[GenerateSerializer]
public sealed record CapabilityTokenRequest
{
    [Id(0)] public string WorkspaceId { get; init; } = string.Empty;
    [Id(1)] public string IssuedTo { get; init; } = string.Empty;
    [Id(2)] public HashSet<string> Grants { get; init; } = [];
    [Id(3)] public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(24);
}
