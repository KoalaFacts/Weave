using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Models;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Events;
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

    private static CapabilityToken InboundToken(
        CapabilityTokenService svc,
        ChannelId? channelId = null,
        string? workspaceId = null,
        TimeSpan? lifetime = null,
        HashSet<string>? grants = null)
    {
        var ch = channelId ?? TestChannelId;
        return svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId ?? TestWorkspaceId.ToString(),
            IssuedTo = "test",
            Grants = grants ?? [$"channel:receive:{ch}", $"channel:send:{ch}"],
            Lifetime = lifetime ?? TimeSpan.FromHours(1)
        });
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

    private static (ChannelGatewayActor Actor, IVirtualActorProvider ActorProvider, IEventBus EventBus, CapabilityTokenService TokenService) CreateActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = NullLogger<ChannelGatewayActor>.Instance;
        var persistentState = CreatePersistentState();
        var tokenService = CreateTokenService();
        var authorizer = new CapabilityAuthorizer(tokenService, eventBus, NullLogger<CapabilityAuthorizer>.Instance);

        var actor = new ChannelGatewayActor(actors, eventBus, authorizer, logger, persistentState);
        return (actor, actors, eventBus, tokenService);
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
        var (actor, actorFactory, _, tokenService) = CreateActor();
        SetupAgentActor(actorFactory, "Hello from agent!");
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage(), InboundToken(tokenService));

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Hello from agent!");
        outbound.ChannelId.ShouldBe(TestChannelId);
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/researcher"));
    }

    [Fact]
    public async Task RouteInboundAsync_UsesRoutingRules_WhenNoTargetAgent()
    {
        var (actor, actorFactory, _, tokenService) = CreateActor();
        SetupAgentActor(actorFactory, "Routed response");
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));
        await actor.SetRoutingRuleAsync("user-123", "support-agent");

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage(), InboundToken(tokenService));

        outbound.ShouldNotBeNull();
        outbound.Content.ShouldBe("Routed response");
        actorFactory.Received(1).GetActor<IAgentActor>(VirtualActorId.From($"{TestWorkspaceId}/support-agent"));
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenNoRouteFound()
    {
        var (actor, _, _, tokenService) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: null));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), InboundToken(tokenService)));
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
        var (actor, actorFactory, _, tokenService) = CreateActor();
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

        var outbound = await actor.RouteInboundAsync(message, InboundToken(tokenService));
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
        var (actor, actorFactory, eventBus, tokenService) = CreateActor();
        SetupAgentActor(actorFactory);
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await actor.RouteInboundAsync(CreateInboundMessage(), InboundToken(tokenService));

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
        var (actor, _, _, tokenService) = CreateActor();
        var unknownChannel = ChannelId.From("ch-unknown");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(
                CreateInboundMessage(channelId: unknownChannel),
                InboundToken(tokenService, channelId: unknownChannel)));
        ex.Message.ShouldContain("not registered");
    }

    [Fact]
    public async Task RouteInboundAsync_ThrowsWhenChannelDisabled()
    {
        var (actor, _, _, tokenService) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher", enabled: false));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), InboundToken(tokenService)));
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

        var tokenService = CreateTokenService();
        var authorizer = new CapabilityAuthorizer(tokenService, eventBus, NullLogger<CapabilityAuthorizer>.Instance);
        var actor = new ChannelGatewayActor(actors, eventBus, authorizer, logger, persistentState);
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

        var tokenService = CreateTokenService();
        var authorizer = new CapabilityAuthorizer(tokenService, eventBus, NullLogger<CapabilityAuthorizer>.Instance);
        var actor = new ChannelGatewayActor(actors, eventBus, authorizer, logger, persistentState);
        await actor.OnActivatedAsync("different-ws", TestContext.Current.CancellationToken);

        state.WorkspaceId.ShouldBe("existing-ws");
    }

    // --- Capability check tests ---

    [Fact]
    public async Task RouteInboundAsync_WithoutChannelReceiveGrant_Throws()
    {
        var (actor, _, _, tokenService) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var sendOnlyToken = InboundToken(tokenService, grants: [$"channel:send:{TestChannelId}"]);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), sendOnlyToken));
    }

    [Fact]
    public async Task RouteInboundAsync_WithoutChannelSendGrant_Throws()
    {
        var (actor, _, _, tokenService) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var receiveOnlyToken = InboundToken(tokenService, grants: [$"channel:receive:{TestChannelId}"]);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), receiveOnlyToken));
    }

    [Fact]
    public async Task RouteInboundAsync_WithExpiredToken_Throws()
    {
        var (actor, _, _, tokenService) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(
                CreateInboundMessage(),
                InboundToken(tokenService, lifetime: TimeSpan.FromMilliseconds(-1))));
    }

    [Fact]
    public async Task RouteInboundAsync_WithCrossWorkspaceToken_Throws()
    {
        var (actor, _, _, tokenService) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(
                CreateInboundMessage(),
                InboundToken(tokenService, workspaceId: "other-workspace")));
    }

    [Fact]
    public async Task RouteInboundAsync_WithReceiveSendWildcardGrants_Allowed()
    {
        var (actor, actorFactory, _, tokenService) = CreateActor();
        SetupAgentActor(actorFactory);
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var wildcard = InboundToken(tokenService, grants: ["channel:receive:*", "channel:send:*"]);

        var outbound = await actor.RouteInboundAsync(CreateInboundMessage(), wildcard);

        outbound.ShouldNotBeNull();
    }

    [Fact]
    public async Task RouteInboundAsync_WithChannelSpecificGrants_AllowsOnlyMatchingChannel()
    {
        var (actor, _, _, tokenService) = CreateActor();
        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var otherChannelToken = InboundToken(tokenService, channelId: ChannelId.From("other-channel"));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), otherChannelToken));
    }

    [Fact]
    public async Task RouteInboundAsync_OnDeniedReceive_PublishesEventWithChannelReceiveGrant()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var eventBus = new CapabilityCapturingEventBus();
        var persistentState = CreatePersistentState();
        var tokenService = CreateTokenService();
        var authorizer = new CapabilityAuthorizer(tokenService, eventBus, NullLogger<CapabilityAuthorizer>.Instance);
        var actor = new ChannelGatewayActor(actors, eventBus, authorizer, NullLogger<ChannelGatewayActor>.Instance, persistentState);

        await actor.RegisterChannelAsync(CreateChannelConfig(targetAgent: "researcher"));
        var sendOnlyToken = InboundToken(tokenService, grants: [$"channel:send:{TestChannelId}"]);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RouteInboundAsync(CreateInboundMessage(), sendOnlyToken));

        eventBus.CapabilityEvents.Count.ShouldBe(1);
        var evt = eventBus.CapabilityEvents[0];
        evt.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        evt.Reason.ShouldBe("grant-missing");
        evt.Grant.ShouldBe($"channel:receive:{TestChannelId}");
        evt.ActionContext.ShouldBe("ChannelGatewayActor.RouteInbound:receive");
    }

    private sealed class CapabilityCapturingEventBus : IEventBus
    {
        public List<CapabilityAuthorizationEvent> CapabilityEvents { get; } = [];

        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
        {
            if (domainEvent is CapabilityAuthorizationEvent capabilityEvent)
                CapabilityEvents.Add(capabilityEvent);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent =>
            throw new NotSupportedException();
    }
}
