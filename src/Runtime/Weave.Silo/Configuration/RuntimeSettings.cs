namespace Weave.Silo.Configuration;

public sealed class RuntimeSettings
{
    public const string SectionName = "Runtime";
    public const string LegacyOrleansSectionName = "Orleans";

    public string? ClusterId { get; set; }

    public static RuntimeSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = configuration
            .GetSection($"{WeaveSettings.SectionName}:{SectionName}")
            .Get<RuntimeSettings>() ?? new RuntimeSettings();

        settings.ClusterId ??= configuration
            .GetSection(LegacyOrleansSectionName)
            .GetValue<string?>(nameof(ClusterId));

        return settings;
    }
}
