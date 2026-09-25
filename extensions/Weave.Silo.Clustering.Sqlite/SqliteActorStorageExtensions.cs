using Orleans.Hosting;

namespace Weave.Silo.Clustering.Sqlite;

public static class SqliteActorStorageExtensions
{
    private const string Invariant = "System.Data.SQLite";

    /// <summary>
    /// Initializes and registers the default Orleans ADO.NET grain storage on SQLite. SQLite is
    /// single-node, so clustering stays on the host's existing
    /// <c>UseLocalhostClustering</c> — only persistence is wired here.
    /// </summary>
    public static ISiloBuilder AddSqliteActorStorage(this ISiloBuilder siloBuilder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        SqliteActorStorageInitializer.Initialize(connectionString);

        siloBuilder.AddAdoNetGrainStorageAsDefault(options =>
        {
            options.ConnectionString = connectionString;
            options.Invariant = Invariant;
        });
        return siloBuilder;
    }
}
