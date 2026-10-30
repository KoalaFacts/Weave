namespace Weave.Silo.Configuration;

public sealed class WeaveSettings
{
    public const string SectionName = "Weave";

    public bool LocalMode { get; set; }
    public bool RequireHttps { get; set; }
    public ActorStorageSettings ActorStorage { get; set; } = new();
    public ContainerRuntimeSettings ContainerRuntime { get; set; } = new();
    public RuntimeSettings Runtime { get; set; } = new();
    public DaprSettings Dapr { get; set; } = new();
    public VaultSettings Vault { get; set; } = new();

    public bool IsLocalMode => LocalMode || string.IsNullOrWhiteSpace(Runtime.ClusterId);

    public static WeaveSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = configuration.GetSection(SectionName).Get<WeaveSettings>() ?? new WeaveSettings();

        settings.ActorStorage = ActorStorageSettings.FromConfiguration(configuration);
        settings.ContainerRuntime = ContainerRuntimeSettings.FromConfiguration(configuration);
        settings.Runtime = RuntimeSettings.FromConfiguration(configuration);
        settings.Dapr = DaprSettings.FromConfiguration(configuration);
        settings.Vault = VaultSettings.FromConfiguration(configuration);

        return settings;
    }
}

