namespace Weave.Silo.Configuration;

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
