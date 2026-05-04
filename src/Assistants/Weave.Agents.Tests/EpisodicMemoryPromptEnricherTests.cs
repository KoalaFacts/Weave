using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class EpisodicMemoryPromptEnricherTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static (EpisodicMemoryPromptEnricher Enricher, IEpisodicMemoryActor Actor) CreateEnricher()
    {
        var actor = Substitute.For<IEpisodicMemoryActor>();
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IEpisodicMemoryActor>(Arg.Any<VirtualActorId>()).Returns(actor);
        var enricher = new EpisodicMemoryPromptEnricher(actors, NullLogger.Instance);
        return (enricher, actor);
    }

    private static Episode CreateEpisode(string id, string title = "Past release", string narrative = "Past narrative") =>
        new()
        {
            EpisodeId = EpisodeId.From(id),
            Title = title,
            Narrative = narrative,
            AgentName = "researcher",
            Tags = ["deploy"],
            Decisions = [],
            OccurredAt = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)
        };

    [Fact]
    public async Task EnrichAsync_AppendsEpisodesBlockToPrompt()
    {
        var (enricher, actor) = CreateEnricher();
        var ep = CreateEpisode("ep-1", title: "Shipped v1");
        actor.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<EpisodeSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<EpisodeSearchResult>>(
                [new EpisodeSearchResult { Episode = ep, RelevanceScore = 4.2 }]));

        var result = await enricher.EnrichAsync(TestWorkspaceId, "researcher", "tell me about deploys", "Base prompt.");

        result.Prompt.ShouldNotBeNull();
        result.Prompt.ShouldContain("Base prompt.");
        result.Prompt.ShouldContain("[Relevant past episodes]");
        result.Prompt.ShouldContain("Shipped v1");
        result.EpisodeIds.Count.ShouldBe(1);
        result.EpisodeIds[0].ShouldBe(ep.EpisodeId);
    }

    [Fact]
    public async Task EnrichAsync_RendersDecisionsAndReviewFeedback_WhenPresent()
    {
        var (enricher, actor) = CreateEnricher();
        var ep = CreateEpisode("ep-dec", title: "Architecture review") with
        {
            Decisions = [
                new EpisodeDecision { Question = "DB choice", ChosenOption = "Postgres" }
            ],
            ReviewFeedback = "ops familiarity outweighed perf concerns"
        };
        actor.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<EpisodeSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<EpisodeSearchResult>>(
                [new EpisodeSearchResult { Episode = ep, RelevanceScore = 5.0 }]));

        var result = await enricher.EnrichAsync(TestWorkspaceId, "researcher", "what did we pick", null);

        result.Prompt.ShouldNotBeNull();
        result.Prompt.ShouldContain("Decisions:");
        result.Prompt.ShouldContain("DB choice -> Postgres");
        result.Prompt.ShouldContain("Review: ops familiarity outweighed perf concerns");
    }

    [Fact]
    public async Task EnrichAsync_ReturnsOriginalPrompt_WhenNoMatches()
    {
        var (enricher, actor) = CreateEnricher();
        actor.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<EpisodeSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<EpisodeSearchResult>>([]));

        var result = await enricher.EnrichAsync(TestWorkspaceId, "researcher", "hello", "Base prompt.");

        result.Prompt.ShouldBe("Base prompt.");
        result.EpisodeIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichAsync_ReturnsOriginalPrompt_WhenActorThrows()
    {
        var (enricher, actor) = CreateEnricher();
        actor.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<EpisodeSearchOptions>())
            .Returns(Task.FromException<IReadOnlyList<EpisodeSearchResult>>(new InvalidOperationException("boom")));

        var result = await enricher.EnrichAsync(TestWorkspaceId, "researcher", "x", "Base prompt.");

        result.Prompt.ShouldBe("Base prompt.");
        result.EpisodeIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichAsync_RecallsWorkspaceWideAndPrefersRecent()
    {
        var (enricher, actor) = CreateEnricher();
        actor.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<EpisodeSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<EpisodeSearchResult>>([]));

        await enricher.EnrichAsync(TestWorkspaceId, "researcher", "anything", null);

        // Recall is workspace-wide (no AgentName filter) so agents share past episodes.
        await actor.Received(1).RecallAsync(
            "anything",
            3,
            Arg.Is<EpisodeSearchOptions>(o => o.AgentName == null && o.PreferRecent));
    }

    [Fact]
    public async Task RecordRecallAsync_CallsActorPerEpisode()
    {
        var (enricher, actor) = CreateEnricher();
        EpisodeId[] ids = [EpisodeId.From("a"), EpisodeId.From("b")];

        await enricher.RecordRecallAsync(TestWorkspaceId, "researcher", ids);

        await actor.Received(1).RecordRecallAsync(ids[0]);
        await actor.Received(1).RecordRecallAsync(ids[1]);
    }

    [Fact]
    public async Task RecordRecallAsync_NoOpForEmptyIdList()
    {
        var (enricher, actor) = CreateEnricher();

        await enricher.RecordRecallAsync(TestWorkspaceId, "researcher", []);

        await actor.DidNotReceive().RecordRecallAsync(Arg.Any<EpisodeId>());
    }

    [Fact]
    public async Task RecordRecallAsync_SwallowsActorException()
    {
        var (enricher, actor) = CreateEnricher();
        actor.RecordRecallAsync(Arg.Any<EpisodeId>())
            .Returns(Task.FromException(new InvalidOperationException("write failed")));

        // Should not throw.
        await enricher.RecordRecallAsync(TestWorkspaceId, "researcher", [EpisodeId.From("x")]);
    }
}
