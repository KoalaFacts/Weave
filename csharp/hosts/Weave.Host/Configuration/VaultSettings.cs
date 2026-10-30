namespace Weave.Silo.Configuration;

public sealed class VaultSettings
{
    public const string SectionName = "Vault";

    public string? Address { get; set; }
    public string? Token { get; set; }

    public static VaultSettings FromConfiguration(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<VaultSettings>() ?? new VaultSettings();
}
