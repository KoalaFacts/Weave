using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.Actors;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Security.Tokens;
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

    private static CapabilityTokenService CreateTokenService() =>
        new(
            Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            TimeProvider.System);

    private static CapabilityToken Token(CapabilityTokenService svc, ChannelId channelId, string workspaceId) =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "test",
            Grants = [$"channel:receive:{channelId}", $"channel:send:{channelId}"],
            Lifetime = TimeSpan.FromHours(1)
        });

    private static CapabilityToken FullToken(CapabilityTokenService svc) =>
        Token(svc, TestChannelId, TestWorkspaceId.ToString());

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

    private static (ChannelGatewayActor Actor, IVirtualActorProvider ActorProvider, IEventBus EventBus, CapabilityToken Token) CreateActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<ChannelGatewayActor>.Instance;
        var persistentState = CreatePersistentState();
        var tokenService = CreateTokenService();

        var actor = new ChannelGatewayActor(actors, eventBus, tokenService, logger, persistentState);
        return (actor, actors, eventBus, FullToken(tokenService));
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
        var (actor, _, _, _) = CreateActor();
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
        var (actor, _, _, _) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await actor.UnregisterChannelAsync(TestChannelId);

        var channels = await actor.GetChannelsAsync();
        channels.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RouteInboundAsync_RoutesToTargetAgent()
    {
        var (actor, actorFactory, _, token) = CreateActor();
        SetupAgentActor(actorFactory, "Hello from agent!");
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage(), token);

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Hello from agent!");
        outbound.ChannelId.ShouldBe(TestChannelId);
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/researcher"));
    }

    [Fact]
    public async Task RouteInboundAsync_UsesRoutingRules_WhenNoTargetAgent()
    {
        var (actor, actorFactory, _, token) = CreateActor();
        SetupAgentActor(actorFactory, "Routed response");
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));
        await actor.SetRoutingRuleAsync("user-123", "support-agent");

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage(), token);

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Routed response");
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/support-agent"));
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenNoRouteFound()
    {
        var (actor, _, _, token) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), token));
        ex.Message.ShouldContain("No route found");
    }

    [Fact]
    public async Task GetChannelsAsync_ReturnsAllChannels()
    {
        var (actor, _, _, _) = CreateActor();
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
        var (actor, actorFactory, _, token) = CreateActor();
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

        var outbound = await actor.RouteInboundAsync(message, token);
        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Matched");
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/greeting-agent"));
    }

    [Fact]
    public async Task RegisterChannelAsync_PublishesChannelRegisteredEvent()
    {
        var (actor, _, eventBus, _) = CreateActor();
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
        var (actor, actorFactory, eventBus, token) = CreateActor();
        SetupAgentActor(actorFactory);
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await actor.RouteInboundAsync(CreateInboundMessage(), token);

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
        var (actor, _, _, _) = CreateActor();
        var unknownChannel = ChannelId.From("ch-unknown");
        var tokenService = CreateTokenService();
        var unknownToken = Token(tokenService, unknownChannel, TestWorkspaceId.ToString());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(channelId: unknownChannel), unknownToken));
        ex.Message.ShouldContain("not registered");
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenChannelDisabled()
    {
        var (actor, _, _, token) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher", enabled: false));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), token));
        ex.Message.ShouldContain("disabled");
    }

    [Fact]
    public async Task OnActivatedAsync_WithKey_SetsWorkspaceId()
    {
        var state = new ChannelGatewayState(); // blank WorkspaceId
        var persistentState = Substitute.For<IActorState<ChannelGatewayState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var actors = Substitute.For<IVirtualActorProvider>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<ChannelGatewayActor>.Instance;

        var actor = new ChannelGatewayActor(actors, eventBus, CreateTokenService(), logger, persistentState);
        await actor.OnActivatedAsync("ws-1", TestContext.Current.CancellationToken);

        state.WorkspaceId.ShouldBe("ws-1");
        await persistentState.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnActivatedAsync_WithExistingWorkspaceId_DoesNotOverwrite()
    {
        var state = new ChannelGatewayState { WorkspaceId = "existing-ws" };
        var persistentState = Substitute.For<IActorState<ChannelGatewayState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var actors = Substitute.For<IVirtualActorProvider>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<ChannelGatewayActor>.Instance;

        var actor = new ChannelGatewayActor(actors, eventBus, CreateTokenService(), logger, persistentState);
        await actor.OnActivatedAsync("different-ws", TestContext.Current.CancellationToken);

        state.WorkspaceId.ShouldBe("existing-ws");
    }

    // --- Capability check tests ---

    private static (ChannelGatewayActor Actor, IVirtualActorProvider ActorProvider, CapabilityTokenService TokenService) CreateActorWithService()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<ChannelGatewayActor>.Instance;
        var persistentState = CreatePersistentState();
        var tokenService = CreateTokenService();

        var actor = new ChannelGatewayActor(actors, eventBus, tokenService, logger, persistentState);
        SetupAgentActor(actors);
        return (actor, actors, tokenService);
    }

    [Fact]
    public async Task RouteInboundAsync_WithoutChannelReceiveGrant_Throws()
    {
        var (actor, _, tokenService) = CreateActorWithService();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var sendOnlyToken = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = [$"channel:send:{TestChannelId}"],
            Lifetime = TimeSpan.FromHours(1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), sendOnlyToken));
    }

    [Fact]
    public async Task RouteInboundAsync_WithoutChannelSendGrant_Throws()
    {
        var (actor, _, tokenService) = CreateActorWithService();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var receiveOnlyToken = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = [$"channel:receive:{TestChannelId}"],
            Lifetime = TimeSpan.FromHours(1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), receiveOnlyToken));
    }

    [Fact]
    public async Task RouteInboundAsync_WithExpiredToken_Throws()
    {
        var (actor, _, tokenService) = CreateActorWithService();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var expired = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = [$"channel:receive:{TestChannelId}", $"channel:send:{TestChannelId}"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), expired));
    }

    [Fact]
    public async Task RouteInboundAsync_WithCrossWorkspaceToken_Throws()
    {
        var (actor, _, tokenService) = CreateActorWithService();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var foreignToken = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "other-workspace",
            IssuedTo = "test",
            Grants = [$"channel:receive:{TestChannelId}", $"channel:send:{TestChannelId}"],
            Lifetime = TimeSpan.FromHours(1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), foreignToken));
    }

    [Fact]
    public async Task RouteInboundAsync_WithReceiveSendWildcardGrants_Allowed()
    {
        var (actor, _, tokenService) = CreateActorWithService();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var wildcard = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = ["channel:receive:*", "channel:send:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage(), wildcard);

        outbound.ShouldNotBeNull();
    }

    [Fact]
    public async Task RouteInboundAsync_WithChannelSpecificGrants_AllowsOnlyMatchingChannel()
    {
        var (actor, _, tokenService) = CreateActorWithService();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var otherChannelToken = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = ["channel:receive:other-channel", "channel:send:other-channel"],
            Lifetime = TimeSpan.FromHours(1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), otherChannelToken));
    }
}
