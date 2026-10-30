namespace Weave.Silo.Configuration;

public sealed class ContainerRuntimeSettings
{
    public const string SectionName = "ContainerRuntime";

    public string Engine { get; set; } = Weave.Workspaces.Runtime.ContainerRuntimeOptions.PodmanEngine;

    public static ContainerRuntimeSettings FromConfiguration(IConfiguration configuration) =>
        configuration
            .GetSection(WeaveSettings.SectionName)
            .GetSection(SectionName)
            .Get<ContainerRuntimeSettings>() ?? new ContainerRuntimeSettings();
}
