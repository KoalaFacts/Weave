using Orleans.Hosting;
using StackExchange.Redis;

namespace Weave.Silo.Clustering.Redis;

public static class RedisActorStorageExtensions
{
    /// <summary>
    /// Registers the default Orleans grain storage on Redis. Connection
    /// string is parsed by <see cref="ConfigurationOptions.Parse(string)"/>;
    /// pass a single endpoint (e.g. <c>"localhost:6379"</c>) or a full
    /// connection string with options.
    /// </summary>
    public static ISiloBuilder AddRedisActorStorage(this ISiloBuilder siloBuilder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        siloBuilder.AddRedisGrainStorageAsDefault(options =>
        {
            options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString);
        });
        return siloBuilder;
    }
}
