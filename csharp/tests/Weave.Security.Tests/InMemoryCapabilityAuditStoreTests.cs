using Microsoft.Extensions.Options;
using Weave.Security.Audit;
using Weave.Security.Events;

namespace Weave.Security.Tests;

public sealed class InMemoryCapabilityAuditStoreTests
{
    private static InMemoryCapabilityAuditStore CreateStore(int capacity = 100) =>
        new(Options.Create(new CapabilityAuditOptions { Capacity = capacity }));

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
        store.Record(Event(
            tokenId: "tok-1",
            grant: "user:write:alice",
            outcome: CapabilityAuthorizationOutcome.Deny,
            reason: "grant-missing",
            actionContext: "UserModelActor.SetPreference",
            workspaceId: "ws-x",
            issuedTo: "agent-y"));

        var row = store.GetByToken("tok-1").Single();

        row.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        row.Reason.ShouldBe("grant-missing");
        row.Grant.ShouldBe("user:write:alice");
        row.ActionContext.ShouldBe("UserModelActor.SetPreference");
        row.WorkspaceId.ShouldBe("ws-x");
        row.IssuedTo.ShouldBe("agent-y");
    }

    [Fact]
    public void Record_NullEvent_Throws()
    {
        var store = CreateStore();
        Should.Throw<ArgumentNullException>(() => store.Record(null!));
    }

    [Fact]
    public void Constructor_CapacityZero_Throws()
    {
        Should.Throw<InvalidOperationException>(
            () => new InMemoryCapabilityAuditStore(Options.Create(new CapabilityAuditOptions { Capacity = 0 })));
    }
}
