using Orleans.Hosting;
using Weave.Silo.Configuration;

namespace Weave.Silo.Startup;

internal sealed class ActorStorageConfigurator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from SiloBuilderConfigurator.")]
    public void Configure(
        ISiloBuilder siloBuilder,
        ActorStorageSettings storageSettings,
        IConfiguration configuration)
    {
        var storage = storageSettings.Provider.ToLowerInvariant();
        var schema = storageSettings.Schema;
        var database = storageSettings.Database;

        switch (storage)
        {
            case ActorStorageSettings.SqliteProvider:
                var sqliteConn = configuration.GetConnectionString(ActorStorageSettings.SqliteConnectionName)
                    ?? DefaultSqlitePath();
                siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
                {
                    options.ConnectionString = sqliteConn;
                    options.Invariant = "Microsoft.Data.Sqlite";
                });
                break;

            case ActorStorageSettings.SqlServerProvider:
                ConfigureSqlServer(siloBuilder, configuration, schema, database);
                break;

            case ActorStorageSettings.PostgreSqlProvider or ActorStorageSettings.PostgresProvider:
                ConfigurePostgreSql(siloBuilder, configuration, schema, database);
                break;

            case ActorStorageSettings.RedisProvider:
                var redisConn = configuration.GetConnectionString(ActorStorageSettings.RedisConnectionName)
                    ?? "localhost:6379";
                siloBuilder.AddRedisGrainStorageAsDefault(options =>
                {
                    options.ConfigurationOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConn);
                });
                break;

            default:
                siloBuilder.AddMemoryGrainStorageAsDefault();
                break;
        }
    }

    private static void ConfigureSqlServer(
        ISiloBuilder siloBuilder,
        IConfiguration configuration,
        string? schema,
        string? database)
    {
        var sqlConn = configuration.GetConnectionString(ActorStorageSettings.SqlServerConnectionName)
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required when Weave actor storage provider is 'sqlserver'.");
        if (!string.IsNullOrWhiteSpace(database))
            sqlConn = AppendIfMissing(sqlConn, $"Database={database}");
        else if (!string.IsNullOrWhiteSpace(schema))
            sqlConn = AppendIfMissing(sqlConn, $"Initial Catalog={schema}");

        siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
        {
            options.ConnectionString = sqlConn;
            options.Invariant = "Microsoft.Data.SqlClient";
        });
        siloBuilder.UseAdoNetClustering(options =>
        {
            options.ConnectionString = sqlConn;
            options.Invariant = "Microsoft.Data.SqlClient";
        });
    }

    private static void ConfigurePostgreSql(
        ISiloBuilder siloBuilder,
        IConfiguration configuration,
        string? schema,
        string? database)
    {
        var pgConn = configuration.GetConnectionString(ActorStorageSettings.PostgreSqlConnectionName)
            ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required when Weave actor storage provider is 'postgresql'.");
        if (!string.IsNullOrWhiteSpace(database))
            pgConn = AppendIfMissing(pgConn, $"Database={database}");
        if (!string.IsNullOrWhiteSpace(schema))
            pgConn = AppendIfMissing(pgConn, $"SearchPath={schema}");

        siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
        {
            options.ConnectionString = pgConn;
            options.Invariant = "Npgsql";
        });
        siloBuilder.UseAdoNetClustering(options =>
        {
            options.ConnectionString = pgConn;
            options.Invariant = "Npgsql";
        });
    }

    private static string AppendIfMissing(string connectionString, string kvPair)
    {
        var key = kvPair.Split('=')[0];
        if (connectionString.Contains(key, StringComparison.OrdinalIgnoreCase))
            return connectionString;

        return connectionString.TrimEnd(';') + ";" + kvPair;
    }

    private static string DefaultSqlitePath()
    {
        var weaveHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

        return $"Data Source={Path.Combine(weaveHome, "weave.db")}";
    }
}
