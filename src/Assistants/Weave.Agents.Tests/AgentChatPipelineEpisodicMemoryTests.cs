using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class AgentChatPipelineEpisodicMemoryTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static AgentState CreateActiveState() =>
        new()
        {
            AgentId = "ws-1/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Active,
            Model = "test-model"
        };

    private static (AgentChatPipeline Pipeline, IChatClient ChatClient, IEpisodicMemoryActor Episodic, List<ChatMessage> Captured) BuildPipeline(Episode? recallHit)
    {
        var captured = new List<ChatMessage>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured.AddRange(call.Arg<IEnumerable<ChatMessage>>());
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    ModelId = "test-model"
                };
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var episodic = Substitute.For<IEpisodicMemoryActor>();
        var hits = recallHit is null
            ? Array.Empty<EpisodeSearchResult>()
            : [new EpisodeSearchResult { Episode = recallHit, RelevanceScore = 4.0 }];
        episodic.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<EpisodeSearchOptions>())
            .Returns(Task.FromResult<IReadOnlyList<EpisodeSearchResult>>(hits));

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IEpisodicMemoryActor>(Arg.Any<VirtualActorId>()).Returns(episodic);

        var pipeline = new AgentChatPipeline(
            actors,
            chatClientFactory,
            TimeProvider.System,
            NullLogger<AgentChatPipeline>.Instance);
        return (pipeline, chatClient, episodic, captured);
    }

    [Fact]
    public async Task ExecuteAsync_InjectsEpisodicMemoryBlockIntoSystemPrompt()
    {
        var ep = new Episode
        {
            EpisodeId = EpisodeId.From("ep-1"),
            Title = "Past canary rollout",
            Narrative = "Rolled at 5% then 25%.",
            AgentName = "researcher",
            Tags = ["deploy"],
            OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var (pipeline, _, _, captured) = BuildPipeline(ep);

        await pipeline.ExecuteAsync(CreateActiveState(), new AgentMessage { Content = "deploy a canary" });

        var systemMsg = captured.FirstOrDefault(m => m.Role == ChatRole.System);
        systemMsg.ShouldNotBeNull();
        systemMsg.Text.ShouldContain("[Relevant past episodes]");
        systemMsg.Text.ShouldContain("Past canary rollout");
    }

    [Fact]
    public async Task ExecuteAsync_WithMatchingEpisodes_RecordsRecall()
    {
        var ep = new Episode
        {
            EpisodeId = EpisodeId.From("ep-recall"),
            Title = "Past rollout",
            Narrative = "...",
            AgentName = "researcher"
        };
        var (pipeline, _, episodic, _) = BuildPipeline(ep);

        await pipeline.ExecuteAsync(CreateActiveState(), new AgentMessage { Content = "rollout" });

        await episodic.Received(1).RecordRecallAsync(ep.EpisodeId);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoMatches_DoesNotInjectBlockOrRecord()
    {
        var (pipeline, _, episodic, captured) = BuildPipeline(recallHit: null);

        await pipeline.ExecuteAsync(CreateActiveState(), new AgentMessage { Content = "hi" });

        var systemMsg = captured.FirstOrDefault(m => m.Role == ChatRole.System);
        if (systemMsg is not null)
            systemMsg.Text.ShouldNotContain("[Relevant past episodes]");
        await episodic.DidNotReceive().RecordRecallAsync(Arg.Any<EpisodeId>());
    }

    [Fact]
    public async Task ExecuteAsync_RecallThrows_SwallowsErrorAndContinues()
    {
        var episodic = Substitute.For<IEpisodicMemoryActor>();
        episodic.RecallAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<EpisodeSearchOptions>())
            .Returns(Task.FromException<IReadOnlyList<EpisodeSearchResult>>(new InvalidOperationException("boom")));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { ModelId = "test-model" });
        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IEpisodicMemoryActor>(Arg.Any<VirtualActorId>()).Returns(episodic);

        var pipeline = new AgentChatPipeline(actors, chatClientFactory, TimeProvider.System, NullLogger<AgentChatPipeline>.Instance);

        var response = await pipeline.ExecuteAsync(CreateActiveState(), new AgentMessage { Content = "anything" });

        response.Content.ShouldBe("ok");
    }
}
