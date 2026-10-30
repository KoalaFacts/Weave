namespace Weave.Silo.Configuration;

public sealed class RuntimeSettings
{
    public const string SectionName = "Runtime";

    public string? ClusterId { get; set; }

    public static RuntimeSettings FromConfiguration(IConfiguration configuration) =>
        configuration
            .GetSection($"{WeaveSettings.SectionName}:{SectionName}")
            .Get<RuntimeSettings>() ?? new RuntimeSettings();
}
