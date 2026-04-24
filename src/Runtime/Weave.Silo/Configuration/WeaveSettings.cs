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

    public sealed class ActorStorageSettings
    {
        public const string SectionName = "ActorStorage";
        public const string LegacyOrleansStorageSectionName = "GrainStorage";
        public const string LegacyProviderKey = "Storage";
        public const string LegacySchemaKey = "StorageSchema";
        public const string LegacyDatabaseKey = "StorageDatabase";

        public const string MemoryProvider = "memory";
        public const string SqliteProvider = "sqlite";
        public const string RedisProvider = "redis";
        public const string SqlServerProvider = "sqlserver";
        public const string PostgreSqlProvider = "postgresql";
        public const string PostgresProvider = "postgres";

        public const string SqliteConnectionName = "Sqlite";
        public const string RedisConnectionName = "Redis";
        public const string SqlServerConnectionName = "SqlServer";
        public const string PostgreSqlConnectionName = "PostgreSql";

        public string Provider { get; set; } = MemoryProvider;
        public string? Schema { get; set; }
        public string? Database { get; set; }

        public static ActorStorageSettings FromConfiguration(IConfiguration configuration)
        {
            var weaveSection = configuration.GetSection(WeaveSettings.SectionName);
            var settings = weaveSection.GetSection(SectionName).Get<ActorStorageSettings>()
                ?? weaveSection.GetSection(LegacyOrleansStorageSectionName).Get<ActorStorageSettings>()
                ?? new ActorStorageSettings();

            settings.Provider = weaveSection[LegacyProviderKey] ?? settings.Provider;
            settings.Schema = weaveSection[LegacySchemaKey] ?? settings.Schema;
            settings.Database = weaveSection[LegacyDatabaseKey] ?? settings.Database;

            return settings;
        }
    }

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

    public sealed class DaprSettings
    {
        public const string HttpPortKey = "DAPR_HTTP_PORT";

        public string? HttpPort { get; set; }

        public static DaprSettings FromConfiguration(IConfiguration configuration) =>
            new()
            {
                HttpPort = configuration[HttpPortKey] ?? Environment.GetEnvironmentVariable(HttpPortKey)
            };
    }

    public sealed class VaultSettings
    {
        public const string SectionName = "Vault";

        public string? Address { get; set; }
        public string? Token { get; set; }

        public static VaultSettings FromConfiguration(IConfiguration configuration) =>
            configuration.GetSection(SectionName).Get<VaultSettings>() ?? new VaultSettings();
    }
}
