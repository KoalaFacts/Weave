using Weave.Agents.Memory;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class EpisodicMemoryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

    private static Episode CreateEpisode(
        string id = "ep-1",
        string title = "Shipped release",
        string narrative = "Steps to deploy the canary at five percent.",
        string agentName = "alice",
        List<string>? tags = null,
        DateTimeOffset? occurredAt = null,
        int recallCount = 0,
        DateTimeOffset? lastRecalledAt = null,
        DateTimeOffset? archivedAt = null) =>
        new()
        {
            EpisodeId = EpisodeId.From(id),
            Title = title,
            Narrative = narrative,
            AgentName = agentName,
            Tags = tags ?? ["deploy"],
            Decisions = [],
            OccurredAt = occurredAt ?? Now,
            RecallCount = recallCount,
            LastRecalledAt = lastRecalledAt,
            ArchivedAt = archivedAt
        };

    private static EpisodicMemoryState StateWith(params Episode[] episodes)
    {
        var state = new EpisodicMemoryState();
        foreach (var episode in episodes)
            state.Episodes[episode.EpisodeId.ToString()] = episode;
        return state;
    }

    [Fact]
    public void Recall_EmptyQuery_ReturnsEmpty()
    {
        var state = StateWith(CreateEpisode());

        var results = state.Recall(string.Empty, maxResults: 10, options: null, now: Now);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Recall_WhitespaceQuery_ReturnsEmpty()
    {
        var state = StateWith(CreateEpisode());

        var results = state.Recall("  \t ", maxResults: 10, options: null, now: Now);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Recall_TagMatchScoresHigherThanTitle()
    {
        var tagOnly = CreateEpisode(id: "tag-only", title: "Unrelated", narrative: "Unrelated", tags: ["deploy"]);
        var titleOnly = CreateEpisode(id: "title-only", title: "Deploy guide", narrative: "Unrelated", tags: ["other"]);

        var results = StateWith(tagOnly, titleOnly).Recall("deploy", maxResults: 10, options: null, now: Now);

        results.Count.ShouldBe(2);
        results[0].Episode.EpisodeId.ShouldBe(tagOnly.EpisodeId);
    }

    [Fact]
    public void Recall_TitleMatchScoresHigherThanNarrative()
    {
        var titleOnly = CreateEpisode(id: "title-only", title: "Deploy guide", narrative: "Unrelated text", tags: ["other"]);
        var narrativeOnly = CreateEpisode(id: "narr-only", title: "Unrelated", narrative: "How to deploy", tags: ["other"]);

        var results = StateWith(titleOnly, narrativeOnly).Recall("deploy", maxResults: 10, options: null, now: Now);

        results.Count.ShouldBe(2);
        results[0].Episode.EpisodeId.ShouldBe(titleOnly.EpisodeId);
    }

    [Fact]
    public void Recall_ArchivedEpisode_IsExcluded()
    {
        var archived = CreateEpisode(id: "archived", archivedAt: Now);

        var results = StateWith(archived).Recall("deploy", maxResults: 10, options: null, now: Now);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Recall_AgentNameFilter_IsCaseInsensitive()
    {
        var alice = CreateEpisode(id: "alice-ep", agentName: "Alice");
        var bob = CreateEpisode(id: "bob-ep", agentName: "BOB");

        var results = StateWith(alice, bob).Recall(
            "deploy",
            maxResults: 10,
            options: new EpisodeSearchOptions { AgentName = "alice" },
            now: Now);

        results.Count.ShouldBe(1);
        results[0].Episode.EpisodeId.ShouldBe(alice.EpisodeId);
    }

    [Fact]
    public void Recall_TagFilter_IsCaseInsensitive()
    {
        var ops = CreateEpisode(id: "ops-ep", tags: ["Deploy", "OPS"]);
        var billing = CreateEpisode(id: "billing-ep", tags: ["deploy", "billing"]);

        var results = StateWith(ops, billing).Recall(
            "deploy",
            maxResults: 10,
            options: new EpisodeSearchOptions { Tag = "ops" },
            now: Now);

        results.Count.ShouldBe(1);
        results[0].Episode.EpisodeId.ShouldBe(ops.EpisodeId);
    }

    [Fact]
    public void Recall_SinceFilter_ExcludesEpisodesBeforeCutoff()
    {
        var cutoff = Now.AddDays(-7);
        var atCutoff = CreateEpisode(id: "at", occurredAt: cutoff);
        var before = CreateEpisode(id: "before", occurredAt: cutoff.AddSeconds(-1));
        var after = CreateEpisode(id: "after", occurredAt: cutoff.AddSeconds(1));

        var results = StateWith(atCutoff, before, after).Recall(
            "deploy",
            maxResults: 10,
            options: new EpisodeSearchOptions { Since = cutoff },
            now: Now);

        results.Select(r => r.Episode.EpisodeId.ToString())
            .ShouldBe(new[] { atCutoff.EpisodeId.ToString(), after.EpisodeId.ToString() }, ignoreOrder: true);
    }

    [Fact]
    public void Recall_RecencyBoost_AppliesAtSevenDayBoundary()
    {
        var atBoundary = CreateEpisode(id: "boundary", occurredAt: Now.AddDays(-7));
        var beyondBoundary = CreateEpisode(id: "beyond", occurredAt: Now.AddDays(-7).AddSeconds(-1));
        var options = new EpisodeSearchOptions { PreferRecent = true };

        var results = StateWith(atBoundary, beyondBoundary).Recall("deploy", maxResults: 10, options, Now);

        results[0].Episode.EpisodeId.ShouldBe(atBoundary.EpisodeId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public void Recall_RecencyBoost_AppliesAtThirtyDayBoundary()
    {
        var atBoundary = CreateEpisode(id: "boundary", occurredAt: Now.AddDays(-30));
        var beyondBoundary = CreateEpisode(id: "beyond", occurredAt: Now.AddDays(-30).AddSeconds(-1));
        var options = new EpisodeSearchOptions { PreferRecent = true };

        var results = StateWith(atBoundary, beyondBoundary).Recall("deploy", maxResults: 10, options, Now);

        results[0].Episode.EpisodeId.ShouldBe(atBoundary.EpisodeId);
        results[0].RelevanceScore.ShouldBeGreaterThan(results[1].RelevanceScore);
    }

    [Fact]
    public void Recall_RecencyAnchor_PrefersLastRecalledAtOverOccurredAt()
    {
        var oldButRecentlyRecalled = CreateEpisode(
            id: "recalled",
            occurredAt: Now.AddDays(-100),
            lastRecalledAt: Now.AddDays(-1));
        var newerButNeverRecalled = CreateEpisode(
            id: "fresh",
            occurredAt: Now.AddDays(-20),
            lastRecalledAt: null);
        var options = new EpisodeSearchOptions { PreferRecent = true };

        var results = StateWith(oldButRecentlyRecalled, newerButNeverRecalled).Recall("deploy", maxResults: 10, options, Now);

        results[0].Episode.EpisodeId.ShouldBe(oldButRecentlyRecalled.EpisodeId);
    }

    [Fact]
    public void Recall_RecencyBoost_NotAppliedWhenAnchorInFuture()
    {
        var future = CreateEpisode(id: "future", occurredAt: Now.AddDays(1));
        var stale = CreateEpisode(id: "stale", occurredAt: Now.AddDays(-100));
        var options = new EpisodeSearchOptions { PreferRecent = true };

        var results = StateWith(future, stale).Recall("deploy", maxResults: 10, options, Now);

        results.Count.ShouldBe(2);
        results[0].RelevanceScore.ShouldBe(results[1].RelevanceScore);
    }

    [Fact]
    public void Recall_RecallCount_BoostsScoreLogarithmically()
    {
        var unused = CreateEpisode(id: "unused", recallCount: 0);
        var hot = CreateEpisode(id: "hot", recallCount: 7);

        var results = StateWith(unused, hot).Recall("deploy", maxResults: 10, options: null, now: Now);

        results[0].Episode.EpisodeId.ShouldBe(hot.EpisodeId);
        results[0].RelevanceScore.ShouldBe(results[1].RelevanceScore + Math.Log2(8), tolerance: 1e-9);
    }

    [Fact]
    public void Recall_RespectsMaxResults()
    {
        var episodes = Enumerable.Range(0, 5)
            .Select(i => CreateEpisode(id: $"ep-{i}", recallCount: i))
            .ToArray();

        var results = StateWith(episodes).Recall("deploy", maxResults: 2, options: null, now: Now);

        results.Count.ShouldBe(2);
    }
}
