using System.Text.Json.Serialization;

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

    /// <summary>
    /// Local-process cancellation tied to this token's lifetime: fires on parent
    /// request cancellation, expiry, or revocation. Not serialized — crossing a
    /// grain boundary loses the linkage and defaults to <see cref="CancellationToken.None"/>
    /// on the receiving side. Most actors are co-located, so cancellation propagates
    /// normally; cross-silo calls lose live cancellation but signature/expiry/revocation
    /// still gate via <see cref="ICapabilityTokenService.Validate"/>.
    /// </summary>
    [JsonIgnore]
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    public bool HasGrant(string grant)
    {
        if (Grants.Contains(grant) || Grants.Contains("*"))
            return true;

        var lastColon = grant.LastIndexOf(':');
        while (lastColon > 0)
        {
            if (Grants.Contains(grant[..lastColon] + ":*"))
                return true;
            lastColon = grant.LastIndexOf(':', lastColon - 1);
        }

        return false;
    }
}
