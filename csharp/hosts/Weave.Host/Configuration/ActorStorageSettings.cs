namespace Weave.Silo.Configuration;

public sealed class ActorStorageSettings
{
    public const string SectionName = "ActorStorage";

    public const string MemoryProvider = "memory";
    public const string SqliteProvider = "sqlite";
    public const string RedisProvider = "redis";
    public const string SqlServerProvider = "sqlserver";
    public const string PostgreSqlProvider = "postgresql";

    public const string SqliteConnectionName = "Sqlite";
    public const string RedisConnectionName = "Redis";
    public const string SqlServerConnectionName = "SqlServer";
    public const string PostgreSqlConnectionName = "PostgreSql";

    public string Provider { get; set; } = MemoryProvider;
    public string? Schema { get; set; }
    public string? Database { get; set; }

    public static ActorStorageSettings FromConfiguration(IConfiguration configuration) =>
        configuration
            .GetSection(WeaveSettings.SectionName)
            .GetSection(SectionName)
            .Get<ActorStorageSettings>() ?? new ActorStorageSettings();
}
