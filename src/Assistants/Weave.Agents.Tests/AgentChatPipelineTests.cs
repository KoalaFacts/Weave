using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class AgentChatPipelineTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static AgentState CreateActiveState() =>
        new()
        {
            AgentId = "ws-1/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Active,
            Model = "claude-sonnet-4-20250514"
        };

    private static (AgentChatPipeline Pipeline, IChatClient ChatClient) CreatePipeline()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello back"))
            {
                ModelId = "claude-sonnet-4-20250514"
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var grainFactory = Substitute.For<IGrainFactory>();
        var logger = NullLogger<AgentChatPipeline>.Instance;

        var pipeline = new AgentChatPipeline(grainFactory, chatClientFactory, logger);
        return (pipeline, chatClient);
    }

    [Fact]
    public async Task ExecuteAsync_AddsUserMessageToHistory()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        state.History.ShouldContain(m => m.Role == "user" && m.Content == "Hello");
    }

    [Fact]
    public async Task ExecuteAsync_CallsChatClient()
    {
        var (pipeline, chatClient) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsResponseContent()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        var response = await pipeline.ExecuteAsync(state, message);

        response.Content.ShouldBe("Hello back");
        response.Model.ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task ExecuteAsync_AddsResponseToHistory()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        state.History.ShouldContain(m => m.Role == "assistant" && m.Content == "Hello back");
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesLastActive()
    {
        var (pipeline, _) = CreatePipeline();
        var state = CreateActiveState();
        var before = DateTimeOffset.UtcNow;
        var message = new AgentMessage { Content = "Hello" };

        await pipeline.ExecuteAsync(state, message);

        state.LastActive.ShouldNotBeNull();
        state.LastActive!.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void Initialize_CreatesChatClient()
    {
        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Substitute.For<IChatClient>());
        var grainFactory = Substitute.For<IGrainFactory>();
        var pipeline = new AgentChatPipeline(grainFactory, chatClientFactory, NullLogger<AgentChatPipeline>.Instance);

        pipeline.Initialize("ws-1/researcher", "claude-sonnet-4-20250514");

        chatClientFactory.Received(1).Create("ws-1/researcher", "claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task ExecuteAsync_AfterReset_RecreatesChatClient()
    {
        var (pipeline, chatClient) = CreatePipeline();
        var state = CreateActiveState();

        pipeline.Initialize("ws-1/researcher", "claude-sonnet-4-20250514");
        pipeline.Reset();
        await pipeline.ExecuteAsync(state, new AgentMessage { Content = "Hello" });

        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithConnectedTools_ResolvesFromRegistry()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"))
            {
                ModelId = "claude-sonnet-4-20250514"
            });

        var chatClientFactory = Substitute.For<IAgentChatClientFactory>();
        chatClientFactory.Create(Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        var registry = Substitute.For<IToolRegistryGrain>();
        registry.ResolveAsync("researcher", "code-search")
            .Returns(Task.FromResult<ToolResolution?>(null));

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IToolRegistryGrain>(Arg.Any<string>(), Arg.Any<string?>()).Returns(registry);

        var pipeline = new AgentChatPipeline(grainFactory, chatClientFactory, NullLogger<AgentChatPipeline>.Instance);

        var state = CreateActiveState();
        state.ConnectedTools.Add("code-search");

        await pipeline.ExecuteAsync(state, new AgentMessage { Content = "Search for X" });

        await registry.Received(1).ResolveAsync("researcher", "code-search");
    }
}
