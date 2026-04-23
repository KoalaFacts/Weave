using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Branch coverage for <see cref="ChannelGatewayGrain"/> paths not hit by the
/// main fixture — disabled channels, workspaceId fallback when no key is set,
/// and ResolveAgentWithRules's content-prefix match.
/// </summary>
public sealed class ChannelGatewayGrainBranchTests
{
    private static IPersistentState<ChannelGatewayState> CreatePersistentState(ChannelGatewayState? initial = null)
    {
        var state = initial ?? new ChannelGatewayState { WorkspaceId = "ws-1" };
        var ps = Substitute.For<IPersistentState<ChannelGatewayState>>();
        ps.State.Returns(state);
        ps.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync().Returns(Task.CompletedTask);
        return ps;
    }

    private static ChannelGatewayGrain CreateGrain(
        ChannelGatewayState? initial = null,
        IGrainFactory? factory = null,
        IEventBus? eventBus = null) =>
        new(
            factory ?? Substitute.For<IGrainFactory>(),
            eventBus ?? Substitute.For<IEventBus>(),
            NullLogger<ChannelGatewayGrain>.Instance,
            CreatePersistentState(initial));

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
        var grain = CreateGrain(state);

        await Should.ThrowAsync<InvalidOperationException>(
            () => grain.RouteInboundAsync(BuildMessage()));
    }

    [Fact]
    public void ResolveAgentWithRules_MatchesOnContentPrefix()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = "ws-1",
            RoutingRules = { ["/help"] = "support-bot" }
        };
        var grain = CreateGrain(state);
        var channel = BuildChannel(targetAgent: null);

        var agent = grain.ResolveAgentWithRules(channel, BuildMessage(content: "/help my order broke"));

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
        var grain = CreateGrain(state);
        var channel = BuildChannel(targetAgent: null);

        var agent = grain.ResolveAgentWithRules(channel, BuildMessage(senderId: "vip-customer-42"));

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
        var grain = CreateGrain(state);
        var channel = BuildChannel(targetAgent: null);

        Should.Throw<InvalidOperationException>(
            () => grain.ResolveAgentWithRules(channel, BuildMessage(content: "nothing matches", senderId: "u-1")));
    }

    [Fact]
    public void ResolveAgentWithRules_TargetAgentSet_UsesTargetRegardlessOfRules()
    {
        var state = new ChannelGatewayState
        {
            WorkspaceId = "ws-1",
            RoutingRules = { ["prefix"] = "other-bot" }
        };
        var grain = CreateGrain(state);
        var channel = BuildChannel(targetAgent: "preferred-bot");

        var agent = grain.ResolveAgentWithRules(channel, BuildMessage(content: "prefix-matched"));

        agent.ShouldBe("preferred-bot", "channel.TargetAgent takes precedence over routing rules");
    }
}
