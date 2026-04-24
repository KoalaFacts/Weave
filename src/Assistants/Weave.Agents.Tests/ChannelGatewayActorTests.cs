using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Events;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class ChannelGatewayActorTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private static readonly ChannelId TestChannelId = ChannelId.From("ch-slack-1");

    private static IActorState<ChannelGatewayState> CreatePersistentState()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = TestWorkspaceId.ToString()
        };

        var persistentState = Substitute.For<IActorState<ChannelGatewayState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.ClearStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
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

    private static (ChannelGatewayActor Actor, IVirtualActorProvider ActorProvider, IEventBus EventBus) CreateActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<ChannelGatewayActor>.Instance;
        var persistentState = CreatePersistentState();

        var actor = new ChannelGatewayActor(actors, eventBus, logger, persistentState);
        return (actor, actors, eventBus);
    }

    private static void SetupAgentActor(IVirtualActorProvider actors, string responseContent = "I can help!")
    {
        var agentActor = Substitute.For<IAgentActor>();
        agentActor.SendAsync(Arg.Any<AgentMessage>())
            .Returns(Task.FromResult(new AgentChatResponse
            {
                Content = responseContent,
                ConversationId = "conv-1"
            }));
        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>()).Returns(agentActor);
    }

    [Fact]
    public async Task RegisterChannelAsync_PersistsConfig()
    {
        var (actor, _, _) = CreateActor();
        var config = CreateChannelConfig(targetAgent: "researcher");

        await actor.RegisterChannelAsync(config);

        var channels = await actor.GetChannelsAsync();
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
        var (actor, _, _) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await actor.UnregisterChannelAsync(TestChannelId);

        var channels = await actor.GetChannelsAsync();
        channels.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RouteInboundAsync_RoutesToTargetAgent()
    {
        var (actor, actorFactory, _) = CreateActor();
        SetupAgentActor(actorFactory, "Hello from agent!");
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage());

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Hello from agent!");
        outbound.ChannelId.ShouldBe(TestChannelId);
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/researcher"));
    }

    [Fact]
    public async Task RouteInboundAsync_UsesRoutingRules_WhenNoTargetAgent()
    {
        var (actor, actorFactory, _) = CreateActor();
        SetupAgentActor(actorFactory, "Routed response");
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));
        await actor.SetRoutingRuleAsync("user-123", "support-agent");

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage());

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Routed response");
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/support-agent"));
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenNoRouteFound()
    {
        var (actor, _, _) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage()));
        ex.Message.ShouldContain("No route found");
    }

    [Fact]
    public async Task GetChannelsAsync_ReturnsAllChannels()
    {
        var (actor, _, _) = CreateActor();
        var channel1 = ChannelId.From("ch-1");
        var channel2 = ChannelId.From("ch-2");
        var channel3 = ChannelId.From("ch-3");
        await actor.RegisterChannelAsync(CreateChannelConfig(channelId: channel1, targetAgent: "a1"));
        await actor.RegisterChannelAsync(CreateChannelConfig(channelId: channel2, targetAgent: "a2"));
        await actor.RegisterChannelAsync(CreateChannelConfig(channelId: channel3, targetAgent: "a3"));

        var channels = await actor.GetChannelsAsync();

        channels.Count.ShouldBe(3);
    }

    [Fact]
    public async Task SetRoutingRuleAsync_PersistsRule()
    {
        var (actor, actorFactory, _) = CreateActor();
        SetupAgentActor(actorFactory, "Matched");
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));

        await actor.SetRoutingRuleAsync("Hello", "greeting-agent");

        var message = new InboundMessage
        {
            ChannelId = TestChannelId,
            SourceChannel = ChannelType.Slack,
            SenderId = "unknown-user",
            SenderName = "Bob",
            Content = "Hello there!"
        };

        var outbound = await actor.RouteInboundAsync(message);
        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Matched");
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/greeting-agent"));
    }

    [Fact]
    public async Task RegisterChannelAsync_PublishesChannelRegisteredEvent()
    {
        var (actor, _, eventBus) = CreateActor();
        var config = CreateChannelConfig(targetAgent: "researcher");

        await actor.RegisterChannelAsync(config);

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
        var (actor, actorFactory, eventBus) = CreateActor();
        SetupAgentActor(actorFactory);
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await actor.RouteInboundAsync(CreateInboundMessage());

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
        var (actor, _, _) = CreateActor();
        var unknownChannel = ChannelId.From("ch-unknown");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(channelId: unknownChannel)));
        ex.Message.ShouldContain("not registered");
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenChannelDisabled()
    {
        var (actor, _, _) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher", enabled: false));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage()));
        ex.Message.ShouldContain("disabled");
    }
}
