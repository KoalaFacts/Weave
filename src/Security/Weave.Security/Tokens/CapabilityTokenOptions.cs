namespace Weave.Security.Tokens;

public sealed class CapabilityTokenOptions
{
    public const string ConfigurationSectionName = "CapabilityTokens";
    public const int MinimumSigningKeyLength = 32;
    public string? SigningKey { get; init; }
    public string? RevocationDirectory { get; init; }
}
