namespace Weave.Security.Tokens;

public sealed class CapabilityTokenOptions
{
    public const string ConfigurationSectionName = "CapabilityTokens";
    public string SigningKey { get; init; } = "weave-development-signing-key-change-me";
    public string? RevocationDirectory { get; init; }
}
