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
        if (Grants.Contains(grant))
            return true;

        foreach (var owned in Grants)
        {
            if (Matches(owned, grant))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Segment-wise match. Each <c>*</c> in <paramref name="owned"/> matches one
    /// requested segment, except a trailing <c>*</c> which matches one or more
    /// trailing segments. Examples: <c>user:*:alice</c> matches <c>user:read:alice</c>;
    /// <c>tool:*</c> matches <c>tool:foo</c> and <c>tool:foo:bar</c>; <c>*</c>
    /// matches anything.
    /// </summary>
    private static bool Matches(string owned, string requested)
    {
        var ownedSegs = owned.Split(':');
        var requestedSegs = requested.Split(':');

        for (var i = 0; i < ownedSegs.Length; i++)
        {
            var seg = ownedSegs[i];
            var isLast = i == ownedSegs.Length - 1;

            if (isLast && seg == "*")
                return requestedSegs.Length > i;

            if (i >= requestedSegs.Length)
                return false;

            if (seg != "*" && seg != requestedSegs[i])
                return false;
        }

        return ownedSegs.Length == requestedSegs.Length;
    }
}
