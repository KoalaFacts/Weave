using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Security.Sqlite;

namespace Weave.Security.Tests;

/// <summary>
/// Coverage for <see cref="SqliteCapabilityAuditStore"/>. Mirrors
/// <see cref="InMemoryCapabilityAuditStoreTests"/> so any contract drift
/// between the two backends shows up as a missing assertion. Uses a
/// shared in-memory SQLite database — the connection in the store
/// keeps the database alive for the test's lifetime.
/// </summary>
public sealed class SqliteCapabilityAuditStoreTests
{
    private static SqliteCapabilityAuditStore CreateStore(int capacity = 100)
    {
        // Each test gets its own in-memory database. Using a unique shared
        // cache name + Cache=Shared lets the store hold the only connection
        // and still resolve the same database within the test.
        var sharedName = $"weave-audit-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={sharedName};Mode=Memory;Cache=Shared";
        return new SqliteCapabilityAuditStore(Options.Create(new CapabilityAuditOptions
        {
            Capacity = capacity,
            Backend = CapabilityAuditOptions.SqliteBackend,
            ConnectionString = connectionString
        }));
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
        using var store = CreateStore();
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
        using var store = CreateStore();
        store.Record(Event(tokenId: "tok-1"));

        store.GetByToken("missing").ShouldBeEmpty();
    }

    [Fact]
    public void Record_ExceedsCapacity_EvictsOldestFirst()
    {
        using var store = CreateStore(capacity: 3);
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
        using var store = CreateStore();
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
        using var store = CreateStore();
        store.Record(Event());
        store.GetRecent(0).ShouldBeEmpty();
    }

    [Fact]
    public void GetByToken_PreservesAllFields()
    {
        using var store = CreateStore();
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
        // Timestamp survives the round-trip to within 1ms (ISO-8601 precision).
        (row.Timestamp - ts).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Record_NullEvent_Throws()
    {
        using var store = CreateStore();
        Should.Throw<ArgumentNullException>(() => store.Record(null!));
    }

    [Fact]
    public void Constructor_CapacityZero_Throws()
    {
        Should.Throw<InvalidOperationException>(
            () => new SqliteCapabilityAuditStore(Options.Create(new CapabilityAuditOptions
            {
                Capacity = 0,
                ConnectionString = "Data Source=:memory:"
            })));
    }

    [Fact]
    public void Record_PersistsAcrossNewStoreInstance_OnSharedDatabase()
    {
        // The whole point of the durable backend: data outlives the
        // store instance. Use a shared in-memory database with an
        // explicit holder connection so the database persists between
        // the two store constructions.
        var sharedName = $"weave-audit-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={sharedName};Mode=Memory;Cache=Shared";

        // Hold a connection open for the duration of the test so the
        // shared in-memory database is not torn down between stores.
        using var holder = new SqliteConnection(connectionString);
        holder.Open();

        var options = Options.Create(new CapabilityAuditOptions
        {
            Capacity = 100,
            Backend = CapabilityAuditOptions.SqliteBackend,
            ConnectionString = connectionString
        });

        using (var store1 = new SqliteCapabilityAuditStore(options))
        {
            store1.Record(Event(tokenId: "tok-1", grant: "tool:durable"));
        }

        using var store2 = new SqliteCapabilityAuditStore(options);
        var rows = store2.GetByToken("tok-1");

        rows.Single().Grant.ShouldBe("tool:durable");
    }

    [Fact]
    public void GetByToken_NullOrWhitespace_Throws()
    {
        using var store = CreateStore();
        Should.Throw<ArgumentException>(() => store.GetByToken(""));
        Should.Throw<ArgumentException>(() => store.GetByToken("   "));
    }
}
