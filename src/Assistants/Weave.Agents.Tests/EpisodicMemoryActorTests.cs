using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Scanning;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class EpisodicMemoryActorTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static IActorState<EpisodicMemoryState> CreatePersistentState()
    {
        var state = new EpisodicMemoryState
        {
            WorkspaceId = TestWorkspaceId.ToString()
        };

        var persistentState = Substitute.For<IActorState<EpisodicMemoryState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.ClearStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (EpisodicMemoryActor Actor, IEventBus EventBus, ILeakScanner Scanner) CreateActor(
        TimeProvider? timeProvider = null,
        ILeakScanner? leakScanner = null)
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<EpisodicMemoryActor>.Instance;
        var persistentState = CreatePersistentState();
        var scanner = leakScanner ?? CreateCleanScanner();
        var actor = new EpisodicMemoryActor(eventBus, scanner, timeProvider ?? TimeProvider.System, logger, persistentState);
        return (actor, eventBus, scanner);
    }

    private static ILeakScanner CreateCleanScanner()
    {
        var scanner = Substitute.For<ILeakScanner>();
        scanner.ScanStringAsync(Arg.Any<string>(), Arg.Any<ScanContext>(), Arg.Any<CancellationToken>())
            .Returns(ScanResult.Clean);
        return scanner;
    }

    private static Episode CreateEpisode(
        string? id = null,
        string title = "Shipped release v1.2",
        string narrative = "Discussed deploy strategy and rolled the canary at 5%.",
        string agentName = "researcher",
        List<string>? tags = null,
        List<EpisodeDecision>? decisions = null,
        DateTimeOffset? occurredAt = null,
        int recallCount = 0,
        DateTimeOffset? lastRecalledAt = null)
    {
        return new Episode
        {
            EpisodeId = EpisodeId.From(id ?? Guid.NewGuid().ToString("N")),
            Title = title,
            Narrative = narrative,
            AgentName = agentName,
            Tags = tags ?? ["deploy", "release"],
            Decisions = decisions ?? [],
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            RecallCount = recallCount,
            LastRecalledAt = lastRecalledAt
        };
    }

    [Fact]
    public async Task StoreEpisodeAsync_PersistsEpisode()
    {
        var (actor, _, _) = CreateActor();
        var episode = CreateEpisode(id: "ep-1");

        var result = await actor.StoreEpisodeAsync(episode);

        result.ShouldNotBeNull();
        result.EpisodeId.ShouldBe(episode.EpisodeId);
        var fetched = await actor.GetEpisodeAsync(episode.EpisodeId);
        fetched.ShouldNotBeNull();
        fetched.Title.ShouldBe("Shipped release v1.2");
    }

    [Fact]
    public async Task StoreEpisodeAsync_PublishesEpisodeStoredEvent()
    {
        var (actor, eventBus, _) = CreateActor();
        var episode = CreateEpisode(id: "ep-evt", title: "Event");

        await actor.StoreEpisodeAsync(episode);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.EpisodeStoredEvent>(e =>
                e.EpisodeId == episode.EpisodeId &&
                e.Title == episode.Title &&
                e.AgentName == episode.AgentName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsync_MatchesByTag()
    {
        var (actor, _, _) = CreateActor();
        var deploy = CreateEpisode(id: "deploy-ep", tags: ["deploy", "release"]);
        var billing = CreateEpisode(id: "billing-ep", title: "Refund flow", narrative: "Refund logic.", tags: ["billing"]);
        await actor.StoreEpisodeAsync(deploy);
        await actor.StoreEpisodeAsync(billing);

        var results = await actor.RecallAsync("deploy");

        results.Count.ShouldBe(1);
        results[0].Episode.EpisodeId.ShouldBe(deploy.EpisodeId);
        results[0].RelevanceScore.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task RecallAsync_MatchesByTitleKeywords()
    {
        var (actor, _, _) = CreateActor();
        var ep = CreateEpisode(id: "title-ep", title: "Database migration retry", tags: ["ops"]);
        await actor.StoreEpisodeAsync(ep);

        var results = await actor.RecallAsync("database migration");

        results.ShouldNotBeEmpty();
        results[0].Episode.EpisodeId.ShouldBe(ep.EpisodeId);
    }

    [Fact]
    public async Task RecallAsync_MatchesByNarrative()
    {
        var (actor, _, _) = CreateActor();
        var ep = CreateEpisode(
            id: "narr-ep",
            title: "Sprint review",
            narrative: "Discussed kubernetes upgrade rollout window.",
            tags: ["sprint"]);
        await actor.StoreEpisodeAsync(ep);

        var results = await actor.RecallAsync("kubernetes");

        results.ShouldNotBeEmpty();
        results[0].Episode.EpisodeId.ShouldBe(ep.EpisodeId);
    }

    [Fact]
    public async Task RecallAsync_ReturnsEmpty_WhenNoMatch()
    {
        var (actor, _, _) = CreateActor();
        await actor.StoreEpisodeAsync(CreateEpisode());

        var results = await actor.RecallAsync("quantum entanglement");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task RecallAsync_ReturnsEmpty_WhenStoreEmpty()
    {
        var (actor, _, _) = CreateActor();

        var results = await actor.RecallAsync("anything");

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task RecallAsync_FiltersByAgentName()
    {
        var (actor, _, _) = CreateActor();
        await actor.StoreEpisodeAsync(CreateEpisode(id: "alice-ep", agentName: "alice", tags: ["deploy"]));
        await actor.StoreEpisodeAsync(CreateEpisode(id: "bob-ep", agentName: "bob", tags: ["deploy"]));

        var results = await actor.RecallAsync("deploy", options: new EpisodeSearchOptions { AgentName = "alice" });

        results.Count.ShouldBe(1);
        results[0].Episode.AgentName.ShouldBe("alice");
    }

    [Fact]
    public async Task RecallAsync_FiltersByTag()
    {
        var (actor, _, _) = CreateActor();
        await actor.StoreEpisodeAsync(CreateEpisode(id: "ops-ep", title: "kubernetes maintenance", tags: ["ops"]));
        await actor.StoreEpisodeAsync(CreateEpisode(id: "dev-ep", title: "kubernetes dev cluster", tags: ["dev"]));

        var results = await actor.RecallAsync("kubernetes", options: new EpisodeSearchOptions { Tag = "ops" });

        results.Count.ShouldBe(1);
        results[0].Episode.Tags.ShouldContain("ops");
    }

    [Fact]
    public async Task RecallAsync_FiltersBySinceDate()
    {
        var (actor, _, _) = CreateActor();
        var cutoff = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        await actor.StoreEpisodeAsync(CreateEpisode(id: "old-ep", tags: ["deploy"], occurredAt: cutoff.AddDays(-30)));
        await actor.StoreEpisodeAsync(CreateEpisode(id: "new-ep", tags: ["deploy"], occurredAt: cutoff.AddDays(10)));

        var results = await actor.RecallAsync("deploy", options: new EpisodeSearchOptions { Since = cutoff });

        results.Count.ShouldBe(1);
        results[0].Episode.EpisodeId.ToString().ShouldBe("new-ep");
    }

    [Fact]
    public async Task RecallAsync_RanksHigherRecallCountFirst()
    {
        var (actor, _, _) = CreateActor();
        var seldom = CreateEpisode(id: "seldom", tags: ["deploy"], recallCount: 1);
        var often = CreateEpisode(id: "often", tags: ["deploy"], recallCount: 50);
        await actor.StoreEpisodeAsync(seldom);
        await actor.StoreEpisodeAsync(often);

        var results = await actor.RecallAsync("deploy");

        results.Count.ShouldBe(2);
        results[0].Episode.EpisodeId.ShouldBe(often.EpisodeId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public async Task RecallAsync_PreferRecentBoostsRecentEpisode()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var (actor, _, _) = CreateActor(fakeTime);
        var stale = CreateEpisode(id: "stale-ep", tags: ["deploy"], occurredAt: now.AddDays(-60));
        var recent = CreateEpisode(id: "recent-ep", tags: ["deploy"], occurredAt: now.AddDays(-2));
        await actor.StoreEpisodeAsync(stale);
        await actor.StoreEpisodeAsync(recent);

        var results = await actor.RecallAsync("deploy", options: new EpisodeSearchOptions { PreferRecent = true });

        results.Count.ShouldBe(2);
        results[0].Episode.EpisodeId.ShouldBe(recent.EpisodeId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public async Task RecallAsync_RespectsMaxResults()
    {
        var (actor, _, _) = CreateActor();
        for (var i = 0; i < 5; i++)
            await actor.StoreEpisodeAsync(CreateEpisode(id: $"ep-{i}", tags: ["deploy"]));

        var results = await actor.RecallAsync("deploy", maxResults: 2);

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RecordRecallAsync_IncrementsCountAndStamps()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var (actor, _, _) = CreateActor(new FakeTimeProvider(now));
        var ep = CreateEpisode(id: "ep-recall");
        await actor.StoreEpisodeAsync(ep);

        await actor.RecordRecallAsync(ep.EpisodeId);

        var fetched = await actor.GetEpisodeAsync(ep.EpisodeId);
        fetched.ShouldNotBeNull();
        fetched.RecallCount.ShouldBe(1);
        fetched.LastRecalledAt.ShouldBe(now);
    }

    [Fact]
    public async Task RecordRecallAsync_NoOpWhenMissing()
    {
        var (actor, _, _) = CreateActor();

        await actor.RecordRecallAsync(EpisodeId.From("missing"));

        var all = await actor.GetAllEpisodesAsync();
        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task ArchiveEpisodeAsync_ExcludesFromRecallAndList()
    {
        var (actor, _, _) = CreateActor();
        var ep = CreateEpisode(id: "archived-ep", title: "Archive deploy", tags: ["deploy"]);
        await actor.StoreEpisodeAsync(ep);

        var archived = await actor.ArchiveEpisodeAsync(ep.EpisodeId);

        archived.ShouldNotBeNull();
        archived.ArchivedAt.ShouldNotBeNull();
        var recalls = await actor.RecallAsync("deploy");
        recalls.ShouldBeEmpty();
        var all = await actor.GetAllEpisodesAsync();
        all.ShouldBeEmpty();
        var direct = await actor.GetEpisodeAsync(ep.EpisodeId);
        direct.ShouldNotBeNull();
        direct.ArchivedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ArchiveEpisodeAsync_ReturnsNull_WhenMissing()
    {
        var (actor, _, _) = CreateActor();

        var result = await actor.ArchiveEpisodeAsync(EpisodeId.From("missing"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RemoveEpisodeAsync_DeletesEpisode()
    {
        var (actor, _, _) = CreateActor();
        var ep = CreateEpisode(id: "ep-remove");
        await actor.StoreEpisodeAsync(ep);

        await actor.RemoveEpisodeAsync(ep.EpisodeId);

        var fetched = await actor.GetEpisodeAsync(ep.EpisodeId);
        fetched.ShouldBeNull();
    }

    [Fact]
    public async Task GetEpisodeAsync_ReturnsNull_WhenNotFound()
    {
        var (actor, _, _) = CreateActor();

        var result = await actor.GetEpisodeAsync(EpisodeId.From("never"));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task StoreEpisodeAsync_RedactsLeaksInNarrativeAndDecisions()
    {
        var scanner = new LeakScanner(NullLogger<LeakScanner>.Instance);
        var (actor, _, _) = CreateActor(leakScanner: scanner);
        var episode = CreateEpisode(
            id: "leaky-ep",
            title: "Routine deploy",
            narrative: "Used token sk-ant-abcdef0123456789ZZZZ to call API.",
            decisions:
            [
                new EpisodeDecision { Question = "Auth header", ChosenOption = "Bearer abcdefghijklmnopqrstuvwxyz0123456789" }
            ]);

        var stored = await actor.StoreEpisodeAsync(episode);

        stored.Narrative.ShouldNotContain("sk-ant-abcdef0123456789ZZZZ");
        stored.Narrative.ShouldContain("***REDACTED***");
        stored.Decisions[0].ChosenOption.ShouldNotContain("abcdefghijklmnopqrstuvwxyz0123456789");
        stored.Decisions[0].ChosenOption.ShouldContain("***REDACTED***");
    }

    [Fact]
    public async Task GetAllEpisodesAsync_ReturnsAllNonArchivedEpisodes()
    {
        var (actor, _, _) = CreateActor();
        await actor.StoreEpisodeAsync(CreateEpisode(id: "ep-a", title: "Alpha"));
        await actor.StoreEpisodeAsync(CreateEpisode(id: "ep-b", title: "Beta"));
        var archived = CreateEpisode(id: "ep-c", title: "Archived");
        await actor.StoreEpisodeAsync(archived);
        await actor.ArchiveEpisodeAsync(archived.EpisodeId);

        var all = await actor.GetAllEpisodesAsync();

        all.Count.ShouldBe(2);
        all.ShouldContain(e => e.Title == "Alpha");
        all.ShouldContain(e => e.Title == "Beta");
    }
}
