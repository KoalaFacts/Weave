using Microsoft.Extensions.Options;
using Weave.Security.Audit;
using Weave.Security.Postgres;

namespace Weave.Security.Tests;

/// <summary>
/// Config-validation coverage for <see cref="PostgresCapabilityAuditStore"/>.
/// SQL semantics are not exercised here — running Postgres in CI requires
/// either a Testcontainers dependency or a hosted database, neither of
/// which the rest of the suite uses today. The SQL shape mirrors
/// <see cref="SqliteCapabilityAuditStore"/> (exhaustively tested in
/// <see cref="SqliteCapabilityAuditStoreTests"/>) and a deployed silo
/// exercises the real connection on startup via schema bootstrap.
/// </summary>
public sealed class PostgresCapabilityAuditStoreTests
{
    [Fact]
    public void Constructor_MissingConnectionString_Throws()
    {
        var options = Options.Create(new CapabilityAuditOptions
        {
            Capacity = 100,
            Backend = CapabilityAuditOptions.PostgreSqlBackend,
            ConnectionString = null
        });

        var ex = Should.Throw<InvalidOperationException>(() => new PostgresCapabilityAuditStore(options));
        ex.Message.ShouldContain("ConnectionString");
    }

    [Fact]
    public void Constructor_BlankConnectionString_Throws()
    {
        var options = Options.Create(new CapabilityAuditOptions
        {
            Capacity = 100,
            Backend = CapabilityAuditOptions.PostgreSqlBackend,
            ConnectionString = "   "
        });

        Should.Throw<InvalidOperationException>(() => new PostgresCapabilityAuditStore(options));
    }

    [Fact]
    public void Constructor_CapacityZero_Throws()
    {
        var options = Options.Create(new CapabilityAuditOptions
        {
            Capacity = 0,
            Backend = CapabilityAuditOptions.PostgreSqlBackend,
            ConnectionString = "Host=localhost;Database=weave_audit;Username=;Password="
        });

        Should.Throw<InvalidOperationException>(() => new PostgresCapabilityAuditStore(options));
    }
}
