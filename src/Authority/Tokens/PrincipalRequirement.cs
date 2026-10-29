namespace Weave.Security.Tokens;

/// <summary>
/// Requirements that <see cref="ICapabilityAuthorizer"/> checks against the token's
/// signed <see cref="PrincipalKind"/> instead of its grant list. They cannot be granted:
/// minting rejects any grant that uses the reserved <c>principal:</c> prefix.
/// </summary>
public static class PrincipalRequirement
{
    public const string Prefix = "principal:";

    /// <summary>The caller must be a human principal, such as an approval decision maker.</summary>
    public const string Human = "principal:human";

    public static bool IsReserved(string grant) => grant.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Operation approval grants (<c>tool:NAME:approve:OP</c>) are only issued to humans.</summary>
    public static bool IsApprovalGrant(string grant) =>
        grant.StartsWith("tool:", StringComparison.Ordinal) && grant.Contains(":approve:", StringComparison.Ordinal);
}
