using Microsoft.Extensions.Options;
using Npgsql;
using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Security.Postgres;

namespace Weave.Security.Tests;

/// <summary>
/// Runs the same contract that <see cref="SqliteCapabilityAuditStoreTests"/>
/// exercises in-process against a real Postgres in a Testcontainers-managed
/// container. Catches SQL-dialect drift between the SQLite and Postgres
/// backends — column-rename mistakes (rowid vs row_id), trim semantics
/// (LIMIT/OFFSET vs LIMIT MAX), TIMESTAMPTZ kind handling — before they
/// reach a multi-silo deployment.
///
/// Each test drops and re-creates the table so they don't bleed into each
/// other; the container is shared across the class for speed.
///
/// If Docker isn't available, every test self-skips with a clear reason
/// rather than failing.
/// </summary>
public sealed class PostgresCapabilityAuditStoreIntegrationTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    public PostgresCapabilityAuditStoreIntegrationTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private PostgresCapabilityAuditStore CreateStore(int capacity = 100)
    {
        if (_fixture.ConnectionString is null)
            Assert.Skip(_fixture.UnavailableReason ?? "Docker daemon not available");

        ResetSchema(_fixture.ConnectionString);

        return new PostgresCapabilityAuditStore(Options.Create(new CapabilityAuditOptions
        {
            Capacity = capacity,
            Backend = CapabilityAuditOptions.PostgreSqlBackend,
            ConnectionString = _fixture.ConnectionString
        }));
    }

    private static void ResetSchema(string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS capability_audit;";
        cmd.ExecuteNonQuery();
    }

    private static CapabilityAuthorizationEvent Event(
        string tokenId = "tok-1",
        string grant = "tool:git",
        CapabilityAuthorizationOutcome outcome = CapabilityAuthorizationOutcome.Allow,
        string? reason = null,
        string actionContext = "Test.Op",
        string workspaceId = "ws-1",
        string issuedTo = "agent",
        DateTimeOffset? timestamp = null) =>
        new()
        {
            SourceId = $"{workspaceId}/{tokenId}",
            TokenId = tokenId,
            Grant = grant,
            IssuedTo = issuedTo,
            WorkspaceId = workspaceId,
            ActionContext = actionContext,
            Outcome = outcome,
            Reason = reason,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public void Record_ThenGetByToken_ReturnsEventsInChronologicalOrder()
    {
        var store = CreateStore();
        var t0 = DateTimeOffset.UtcNow;

        store.Record(Event(tokenId: "tok-1", grant: "tool:a", timestamp: t0));
        store.Record(Event(tokenId: "tok-1", grant: "tool:b", timestamp: t0.AddSeconds(1)));
        store.Record(Event(tokenId: "tok-2", grant: "tool:c", timestamp: t0.AddSeconds(2)));

        var rows = store.GetByToken("tok-1");

        rows.Count.ShouldBe(2);
        rows[0].Grant.ShouldBe("tool:a");
        rows[1].Grant.ShouldBe("tool:b");
    }

    [Fact]
    public void GetByToken_UnknownToken_ReturnsEmpty()
    {
        var store = CreateStore();
        store.Record(Event(tokenId: "tok-1"));

        store.GetByToken("missing").ShouldBeEmpty();
    }

    [Fact]
    public void Record_ExceedsCapacity_EvictsOldestFirst()
    {
        var store = CreateStore(capacity: 3);
        var t0 = DateTimeOffset.UtcNow;

        store.Record(Event(tokenId: "tok-1", grant: "g1", timestamp: t0));
        store.Record(Event(tokenId: "tok-1", grant: "g2", timestamp: t0.AddSeconds(1)));
        store.Record(Event(tokenId: "tok-1", grant: "g3", timestamp: t0.AddSeconds(2)));
        store.Record(Event(tokenId: "tok-1", grant: "g4", timestamp: t0.AddSeconds(3)));

        var rows = store.GetByToken("tok-1");
        rows.Count.ShouldBe(3);
        rows.Select(r => r.Grant).ShouldBe(["g2", "g3", "g4"]);
    }

    [Fact]
    public void GetRecent_ReturnsNewestFirst_BoundedByLimit()
    {
        var store = CreateStore();
        var t0 = DateTimeOffset.UtcNow;

        store.Record(Event(grant: "g1", timestamp: t0));
        store.Record(Event(grant: "g2", timestamp: t0.AddSeconds(1)));
        store.Record(Event(grant: "g3", timestamp: t0.AddSeconds(2)));

        var recent = store.GetRecent(2);
        recent.Count.ShouldBe(2);
        recent[0].Grant.ShouldBe("g3");
        recent[1].Grant.ShouldBe("g2");
    }

    [Fact]
    public void GetRecent_ZeroLimit_ReturnsEmpty()
    {
        var store = CreateStore();
        store.Record(Event());
        store.GetRecent(0).ShouldBeEmpty();
    }

    [Fact]
    public void GetByToken_PreservesAllFields()
    {
        var store = CreateStore();
        var ts = DateTimeOffset.UtcNow;
        store.Record(Event(
            tokenId: "tok-1",
            grant: "user:write:alice",
            outcome: CapabilityAuthorizationOutcome.Deny,
            reason: "grant-missing",
            actionContext: "UserModelActor.SetPreference",
            workspaceId: "ws-x",
            issuedTo: "agent-y",
            timestamp: ts));

        var row = store.GetByToken("tok-1").Single();

        row.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        row.Reason.ShouldBe("grant-missing");
        row.Grant.ShouldBe("user:write:alice");
        row.ActionContext.ShouldBe("UserModelActor.SetPreference");
        row.WorkspaceId.ShouldBe("ws-x");
        row.IssuedTo.ShouldBe("agent-y");
        // Postgres TIMESTAMPTZ has microsecond precision — round-trip
        // tolerance is wider than SQLite (which serializes the full ISO-8601
        // string) but still well under a millisecond.
        (row.Timestamp - ts).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Record_NullEvent_Throws()
    {
        var store = CreateStore();
        Should.Throw<ArgumentNullException>(() => store.Record(null!));
    }

    [Fact]
    public void Record_PersistsAcrossNewStoreInstance()
    {
        // The whole point of the durable backend: data outlives the store
        // instance. Two stores against the same connection string see the
        // same row.
        if (_fixture.ConnectionString is null)
            Assert.Skip(_fixture.UnavailableReason ?? "Docker daemon not available");

        ResetSchema(_fixture.ConnectionString);

        var options = Options.Create(new CapabilityAuditOptions
        {
            Capacity = 100,
            Backend = CapabilityAuditOptions.PostgreSqlBackend,
            ConnectionString = _fixture.ConnectionString
        });

        var store1 = new PostgresCapabilityAuditStore(options);
        store1.Record(Event(tokenId: "tok-1", grant: "tool:durable"));

        var store2 = new PostgresCapabilityAuditStore(options);
        var rows = store2.GetByToken("tok-1");

        rows.Single().Grant.ShouldBe("tool:durable");
    }

    [Fact]
    public void GetByToken_NullOrWhitespace_Throws()
    {
        var store = CreateStore();
        Should.Throw<ArgumentException>(() => store.GetByToken(""));
        Should.Throw<ArgumentException>(() => store.GetByToken("   "));
    }
}
