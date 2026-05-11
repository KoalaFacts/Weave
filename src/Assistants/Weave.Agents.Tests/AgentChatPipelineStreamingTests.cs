using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weave.Agents.Channels;
using Weave.Agents.Chat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Pipeline;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Tests;

public sealed class AgentChatPipelineStreamingTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static CapabilityTokenService CreateTokenService() =>
        new(
            Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            TimeProvider.System);

    private static AgentState CreateActiveState() =>
        new()
        {
            AgentId = "ws-1/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Active,
            Model = "claude-sonnet-4-20250514",
            Definition = new AgentDefinition
            {
                Model = "claude-sonnet-4-20250514",
                Capabilities = ["skill:read", "skill:write"]
            }
        };

    private static (AgentChatPipeline Pipeline, FakeTimeProvider Time) CreatePipeline(params string[] textDeltas)
    {
        var chatClient = new StreamingStubChatClient
        {
            Updates = BuildUpdates(textDeltas)
        };
        var factory = Substitute.For<IAgentChatClientFactory>();
        factory.CreateAsync(Arg.Any<string>(), Arg.Any<AgentDefinition?>(), Arg.Any<CancellationToken>()).Returns(chatClient);
        var actors = Substitute.For<IVirtualActorProvider>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var pipeline = new AgentChatPipeline(actors, factory, CreateTokenService(), time, NullLogger<AgentChatPipeline>.Instance);
        return (pipeline, time);
    }

    private static IReadOnlyList<ChatResponseUpdate> BuildUpdates(string[] textDeltas) =>
        [
            .. textDeltas.Select(text => new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(text)],
                ModelId = "claude-sonnet-4-20250514"
            }),
            new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
                ModelId = "claude-sonnet-4-20250514",
                FinishReason = ChatFinishReason.Stop
            }
        ];

    [Fact]
    public async Task ExecuteStreamingAsync_YieldsTextFramesInOrder_FollowedByCompleteFrame()
    {
        var (pipeline, _) = CreatePipeline("Hello, ", "world", "!");
        var state = CreateActiveState();

        var frames = await CollectAsync(pipeline, state, "Hi there");

        var textFrames = frames.OfType<AgentChatTextFrame>().Select(f => f.Text).ToList();
        textFrames.ShouldBe(["Hello, ", "world", "!"]);
        frames[^1].ShouldBeOfType<AgentChatCompleteFrame>();
        frames.OfType<AgentChatCompleteFrame>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CompleteFrame_HasConcatenatedContent()
    {
        var (pipeline, _) = CreatePipeline("Hello, ", "world", "!");
        var state = CreateActiveState();

        var frames = await CollectAsync(pipeline, state, "Hi there");

        var complete = frames.OfType<AgentChatCompleteFrame>().Single();
        complete.Response.Content.ShouldBe("Hello, world!");
        complete.Response.Model.ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task ExecuteStreamingAsync_AppendsUserMessageToHistory_BeforeYieldingAnyFrame()
    {
        var (pipeline, _) = CreatePipeline("Reply");
        var state = CreateActiveState();

        await foreach (var _ in pipeline.ExecuteStreamingAsync(state, new AgentMessage { Content = "<<USER-MARKER>>" }, TestContext.Current.CancellationToken))
        {
            state.History.ShouldContain(m => m.Role == "user" && m.Content == "<<USER-MARKER>>");
            break;
        }
    }

    [Fact]
    public async Task ExecuteStreamingAsync_AppendsAssistantMessageToHistory_OnCompletion()
    {
        var (pipeline, _) = CreatePipeline("Hello, ", "world");
        var state = CreateActiveState();

        await CollectAsync(pipeline, state, "Hi");

        state.History.ShouldContain(m => m.Role == "assistant" && m.Content == "Hello, world");
    }

    [Fact]
    public async Task ExecuteStreamingAsync_AdvancesLastActive_AfterStreamingCompletes()
    {
        // BuildRequestAsync sets LastActive to the user-message timestamp; FinishAsync
        // overwrites it with the post-LLM-call timestamp. Capture LastActive at the first
        // yielded frame (which is after BuildRequestAsync but before FinishAsync), advance
        // the clock, and assert the post-completion LastActive is strictly later — so the
        // test distinguishes the FinishAsync update from the BuildRequestAsync one.
        var (pipeline, time) = CreatePipeline("Reply");
        var state = CreateActiveState();

        DateTimeOffset? duringStreaming = null;
        await foreach (var _ in pipeline.ExecuteStreamingAsync(state, new AgentMessage { Content = "Hi" }, TestContext.Current.CancellationToken))
        {
            if (duringStreaming is null)
            {
                duringStreaming = state.LastActive;
                time.Advance(TimeSpan.FromSeconds(30));
            }
        }

        duringStreaming.ShouldNotBeNull();
        state.LastActive.ShouldNotBeNull();
        state.LastActive!.Value.ShouldBeGreaterThan(duringStreaming.Value);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_NoTextDeltas_EmitsOnlyCompleteFrameWithEmptyContent()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();

        var frames = await CollectAsync(pipeline, state, "Hi");

        frames.OfType<AgentChatTextFrame>().ShouldBeEmpty();
        var complete = frames.OfType<AgentChatCompleteFrame>().Single();
        complete.Response.Content.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_EmptyTextContent_NotYieldedAsTextFrame()
    {
        var (pipeline, _) = CreatePipeline("real-text", string.Empty, "more-real");
        var state = CreateActiveState();

        var frames = await CollectAsync(pipeline, state, "Hi");

        frames.OfType<AgentChatTextFrame>().Select(f => f.Text).ShouldBe(["real-text", "more-real"]);
    }

    private static async Task<List<AgentChatStreamingFrame>> CollectAsync(AgentChatPipeline pipeline, AgentState state, string userContent)
    {
        var frames = new List<AgentChatStreamingFrame>();
        await foreach (var frame in pipeline.ExecuteStreamingAsync(
            state,
            new AgentMessage { Content = userContent },
            TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }
        return frames;
    }
}
