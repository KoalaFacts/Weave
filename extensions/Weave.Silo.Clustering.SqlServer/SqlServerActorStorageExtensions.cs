using Orleans.Hosting;

namespace Weave.Silo.Clustering.SqlServer;

public static class SqlServerActorStorageExtensions
{
    private const string Invariant = "Microsoft.Data.SqlClient";

    /// <summary>
    /// Registers the default Orleans ADO.NET grain storage and clustering on
    /// SQL Server. Both share <paramref name="connectionString"/>.
    /// </summary>
    public static ISiloBuilder AddSqlServerActorStorage(this ISiloBuilder siloBuilder, string connectionString)
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
