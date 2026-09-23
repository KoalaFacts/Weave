using Orleans.Hosting;

namespace Weave.Silo.Clustering.Postgres;

public static class PostgresActorStorageExtensions
{
    private const string Invariant = "Npgsql";

    /// <summary>
    /// Registers the default Orleans ADO.NET grain storage and clustering on
    /// PostgreSQL. Both share <paramref name="connectionString"/>.
    /// </summary>
    public static ISiloBuilder AddPostgresActorStorage(this ISiloBuilder siloBuilder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
        {
            options.ConnectionString = connectionString;
            options.Invariant = Invariant;
        });
        siloBuilder.UseAdoNetClustering(options =>
        {
            options.ConnectionString = connectionString;
            options.Invariant = Invariant;
        });
        return siloBuilder;
    }
}
