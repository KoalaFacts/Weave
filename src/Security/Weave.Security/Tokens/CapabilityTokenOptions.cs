namespace Weave.Security.Tokens;

public sealed class CapabilityTokenOptions
{
    public const string ConfigurationSectionName = "CapabilityTokens";
    public const int MinimumSigningKeyLength = 32;
    public string? SigningKey { get; init; }

    /// <summary>
    /// Optional verify-only key for in-flight token validation across a
    /// signing-key rotation. Subject to the same minimum length.
    /// </summary>
    /// <remarks>
    /// Set <see cref="PreviousSigningKey"/> to the outgoing value at the
    /// moment <see cref="SigningKey"/> is replaced with the new one. Tokens
    /// minted before the swap continue to validate until they expire; new
    /// tokens are signed with — and validate under — <see cref="SigningKey"/>
    /// only. Mint never uses this field.
    /// </remarks>
    public string? PreviousSigningKey { get; init; }

    public string? RevocationDirectory { get; init; }
}
