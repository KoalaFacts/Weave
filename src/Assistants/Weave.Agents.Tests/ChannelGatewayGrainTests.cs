using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Events;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class ChannelGatewayGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private static readonly ChannelId TestChannelId = ChannelId.From("ch-slack-1");

    private static IPersistentState<ChannelGatewayState> CreatePersistentState()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = TestWorkspaceId.ToString()
        };

        var persistentState = Substitute.For<IPersistentState<ChannelGatewayState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static ChannelConfig CreateChannelConfig(
        ChannelId? channelId = null,
        string? targetAgent = null,
        bool enabled = true) =>
        new()
        {
            ChannelId = channelId ?? TestChannelId,
            Type = ChannelType.Slack,
            Name = "general",
            TargetAgent = targetAgent,
            Enabled = enabled
        };

    private static InboundMessage CreateInboundMessage(ChannelId? channelId = null) =>
        new()
        {
            ChannelId = channelId ?? TestChannelId,
            SourceChannel = ChannelType.Slack,
            SenderId = "user-123",
            SenderName = "Alice",
            Content = "Hello agent"
        };

    private static (ChannelGatewayGrain Grain, IGrainFactory GrainFactory, IEventBus EventBus) CreateGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<ChannelGatewayGrain>.Instance;
        var persistentState = CreatePersistentState();

        var grain = new ChannelGatewayGrain(grainFactory, eventBus, logger, persistentState);
        return (grain, grainFactory, eventBus);
    }

    private static void SetupAgentGrain(IGrainFactory grainFactory, string responseContent = "I can help!")
    {
        var agentGrain = Substitute.For<IAgentGrain>();
        agentGrain.SendAsync(Arg.Any<AgentMessage>())
            .Returns(Task.FromResult(new AgentChatResponse
            {
                Content = responseContent,
                ConversationId = "conv-1"
            }));
        grainFactory.GetGrain<IAgentGrain>(Arg.Any<string>(), null).Returns(agentGrain);
    }

    [Fact]
    public async Task RegisterChannelAsync_PersistsConfig()
    {
        var (grain, _, _) = CreateGrain();
        var config = CreateChannelConfig(targetAgent: "researcher");

        await grain.RegisterChannelAsync(config);

        var channels = await grain.GetChannelsAsync();
        channels.Count.ShouldBe(1);
        var channel = channels[0];
        channel.ChannelId.ShouldBe(TestChannelId);
        channel.Type.ShouldBe(ChannelType.Slack);
        channel.Name.ShouldBe("general");
        channel.TargetAgent.ShouldNotBeNull();
        channel.TargetAgent.ShouldBe("researcher");
    }

    [Fact]
    public async Task UnregisterChannelAsync_RemovesChannel()
    {
        var (grain, _, _) = CreateGrain();
        await grain.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await grain.UnregisterChannelAsync(TestChannelId);

        var channels = await grain.GetChannelsAsync();
        channels.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RouteInboundAsync_RoutesToTargetAgent()
    {
        var (grain, grainFactory, _) = CreateGrain();
        SetupAgentGrain(grainFactory, "Hello from agent!");
        await grain.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        var outbound = await grain.RouteInboundAsync(CreateInboundMessage());

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Hello from agent!");
        outbound.ChannelId.ShouldBe(TestChannelId);
        grainFactory.Received(1).GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null);
    }

    [Fact]
    public async Task RouteInboundAsync_UsesRoutingRules_WhenNoTargetAgent()
    {
        var (grain, grainFactory, _) = CreateGrain();
        SetupAgentGrain(grainFactory, "Routed response");
        await grain.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));
        await grain.SetRoutingRuleAsync("user-123", "support-agent");

        var outbound = await grain.RouteInboundAsync(CreateInboundMessage());

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Routed response");
        grainFactory.Received(1).GetGrain<IAgentGrain>($"{TestWorkspaceId}/support-agent", null);
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenNoRouteFound()
    {
        var (grain, _, _) = CreateGrain();
        await grain.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => grain.RouteInboundAsync(CreateInboundMessage()));
        ex.Message.ShouldContain("No route found");
    }

    [Fact]
    public async Task GetChannelsAsync_ReturnsAllChannels()
    {
        var (grain, _, _) = CreateGrain();
        var channel1 = ChannelId.From("ch-1");
        var channel2 = ChannelId.From("ch-2");
        var channel3 = ChannelId.From("ch-3");
        await grain.RegisterChannelAsync(CreateChannelConfig(channelId: channel1, targetAgent: "a1"));
        await grain.RegisterChannelAsync(CreateChannelConfig(channelId: channel2, targetAgent: "a2"));
        await grain.RegisterChannelAsync(CreateChannelConfig(channelId: channel3, targetAgent: "a3"));

        var channels = await grain.GetChannelsAsync();

        channels.Count.ShouldBe(3);
    }

    [Fact]
    public async Task SetRoutingRuleAsync_PersistsRule()
    {
        var (grain, grainFactory, _) = CreateGrain();
        SetupAgentGrain(grainFactory, "Matched");
        await grain.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));

        await grain.SetRoutingRuleAsync("Hello", "greeting-agent");

        var message = new InboundMessage
        {
            ChannelId = TestChannelId,
            SourceChannel = ChannelType.Slack,
            SenderId = "unknown-user",
            SenderName = "Bob",
            Content = "Hello there!"
        };

        var outbound = await grain.RouteInboundAsync(message);
        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Matched");
        grainFactory.Received(1).GetGrain<IAgentGrain>($"{TestWorkspaceId}/greeting-agent", null);
    }

    [Fact]
    public async Task RegisterChannelAsync_PublishesChannelRegisteredEvent()
    {
        var (grain, _, eventBus) = CreateGrain();
        var config = CreateChannelConfig(targetAgent: "researcher");

        await grain.RegisterChannelAsync(config);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<ChannelRegisteredEvent>(e =>
                e.ChannelId == TestChannelId &&
                e.Type == ChannelType.Slack &&
                e.Name == "general"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteInboundAsync_PublishesReceivedAndSentEvents()
    {
        var (grain, grainFactory, eventBus) = CreateGrain();
        SetupAgentGrain(grainFactory);
        await grain.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await grain.RouteInboundAsync(CreateInboundMessage());

        await eventBus.Received(1).PublishAsync(
            Arg.Is<ChannelMessageReceivedEvent>(e =>
                e.ChannelId == TestChannelId &&
                e.SenderId == "user-123" &&
                e.AgentName == "researcher"),
            Arg.Any<CancellationToken>());

        await eventBus.Received(1).PublishAsync(
            Arg.Is<ChannelMessageSentEvent>(e =>
                e.ChannelId == TestChannelId &&
                e.AgentName == "researcher"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenChannelNotRegistered()
    {
        var (grain, _, _) = CreateGrain();
        var unknownChannel = ChannelId.From("ch-unknown");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => grain.RouteInboundAsync(CreateInboundMessage(channelId: unknownChannel)));
        ex.Message.ShouldContain("not registered");
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenChannelDisabled()
    {
        var (grain, _, _) = CreateGrain();
        await grain.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher", enabled: false));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => grain.RouteInboundAsync(CreateInboundMessage()));
        ex.Message.ShouldContain("disabled");
    }
}
