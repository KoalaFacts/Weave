namespace Weave.Silo.Configuration;

public sealed class ContainerRuntimeSettings
{
    public const string SectionName = "ContainerRuntime";
    public const string LegacyRuntimeSectionName = "Runtime";
    public const string LegacyEngineKey = "ContainerEngine";

    public string Engine { get; set; } = Weave.Workspaces.Runtime.ContainerRuntimeOptions.PodmanEngine;

    public static ContainerRuntimeSettings FromConfiguration(IConfiguration configuration)
    {
        var weaveSection = configuration.GetSection(WeaveSettings.SectionName);
        var settings = weaveSection.GetSection(SectionName).Get<ContainerRuntimeSettings>()
            ?? new ContainerRuntimeSettings();

        settings.Engine = weaveSection.GetSection(LegacyRuntimeSectionName)[LegacyEngineKey] ?? settings.Engine;

        return settings;
    }
}
