using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Branch coverage for <see cref="ChannelGatewayActor"/> paths not hit by the
/// main fixture — disabled channels, workspaceId fallback when no key is set,
/// and ResolveAgentWithRules's content-prefix match.
/// </summary>
public sealed class ChannelGatewayActorBranchTests
{
    private static IActorState<ChannelGatewayState> CreatePersistentState(ChannelGatewayState? initial = null)
    {
        var state = initial ?? new ChannelGatewayState { WorkspaceId = "ws-1" };
        var ps = Substitute.For<IActorState<ChannelGatewayState>>();
        ps.State.Returns(state);
        ps.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return ps;
    }

    private static CapabilityTokenService CreateTokenService() =>
        new(
            Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            TimeProvider.System);

    private static CapabilityToken Token(CapabilityTokenService svc, string channelId = "channel-1", string workspaceId = "ws-1") =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = "test",
            Grants = [$"channel:receive:{channelId}", $"channel:send:{channelId}"],
            Lifetime = TimeSpan.FromHours(1)
        });

    private static ChannelGatewayActor CreateActor(
        ChannelGatewayState? initial = null,
        IVirtualActorProvider? actors = null,
        IEventBus? eventBus = null,
        ICapabilityTokenService? tokenService = null)
    {
        var bus = eventBus ?? Substitute.For<IEventBus>();
        var authorizer = new CapabilityAuthorizer(
            tokenService ?? CreateTokenService(),
            bus,
            NullLogger<CapabilityAuthorizer>.Instance);
        return new ChannelGatewayActor(
            actors ?? Substitute.For<IVirtualActorProvider>(),
            bus,
            authorizer,
            NullLogger<ChannelGatewayActor>.Instance,
            CreatePersistentState(initial));
    }

    private static ChannelConfig BuildChannel(
        bool enabled = true,
        string? targetAgent = null,
        ChannelType type = ChannelType.Slack) =>
        new()
        {
            ChannelId = ChannelId.From("channel-1"),
            Type = type,
            Name = "c1",
            Enabled = enabled,
            TargetAgent = targetAgent
        };

    private static InboundMessage BuildMessage(string content = "hello", string senderId = "u-1") =>
        new()
        {
            ChannelId = ChannelId.From("channel-1"),
            SourceChannel = ChannelType.Slack,
            SenderId = senderId,
            SenderName = "alice",
            Content = content
        };

    [Fact]
    public async Task RouteInbound_DisabledChannel_ThrowsInvalidOperation()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = "ws-1",
            Channels = { ["channel-1"] = BuildChannel(enabled: false, targetAgent: "agent-a") }
        };
        var tokenService = CreateTokenService();
        var actor = CreateActor(state, tokenService: tokenService);

        await Should.ThrowAsync<InvalidOperationException>(
            () => actor.RouteInboundAsync(BuildMessage(), Token(tokenService)));
    }

    [Fact]
    public void ResolveAgentWithRules_MatchesOnContentPrefix()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = "ws-1",
            RoutingRules = { ["/help"] = "support-bot" }
        };
        var actor = CreateActor(state);
        var channel = BuildChannel(targetAgent: null);

        var agent = actor.ResolveAgentWithRules(channel, BuildMessage(content: "/help my order broke"));

        agent.ShouldBe("support-bot");
    }

    [Fact]
    public void ResolveAgentWithRules_MatchesOnSenderId()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = "ws-1",
            RoutingRules = { ["vip-"] = "priority-bot" }
        };
        var actor = CreateActor(state);
        var channel = BuildChannel(targetAgent: null);

        var agent = actor.ResolveAgentWithRules(channel, BuildMessage(senderId: "vip-customer-42"));

        agent.ShouldBe("priority-bot");
    }

    [Fact]
    public void ResolveAgentWithRules_NoMatch_Throws()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = "ws-1",
            RoutingRules = { ["something-else"] = "bot" }
        };
        var actor = CreateActor(state);
        var channel = BuildChannel(targetAgent: null);

        Should.Throw<InvalidOperationException>(
            () => actor.ResolveAgentWithRules(channel, BuildMessage(content: "nothing matches", senderId: "u-1")));
    }

    [Fact]
    public void ResolveAgentWithRules_TargetAgentSet_UsesTargetRegardlessOfRules()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = "ws-1",
            RoutingRules = { ["prefix"] = "other-bot" }
        };
        var actor = CreateActor(state);
        var channel = BuildChannel(targetAgent: "preferred-bot");

        var agent = actor.ResolveAgentWithRules(channel, BuildMessage(content: "prefix-matched"));

        agent.ShouldBe("preferred-bot", "channel.TargetAgent takes precedence over routing rules");
    }
}
