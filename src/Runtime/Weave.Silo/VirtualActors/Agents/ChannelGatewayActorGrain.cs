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

namespace Weave.Silo.VirtualActors;

public sealed class ChannelGatewayActorGrain : Grain, IChannelGatewayActorGrain
{
    private readonly ChannelGatewayActor _actor;

    public ChannelGatewayActorGrain(
        IVirtualActorProvider actors,
        IEventBus eventBus,
        ICapabilityAuthorizer authorizer,
        ILogger<ChannelGatewayActor> logger,
        [PersistentState("channel-gateway", "Default")] IPersistentState<ChannelGatewayState> state)
    {
        _actor = new ChannelGatewayActor(actors, eventBus, authorizer, logger,
            new OrleansActorState<ChannelGatewayState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RegisterChannelAsync(ChannelConfig config) => _actor.RegisterChannelAsync(config);
    public Task UnregisterChannelAsync(ChannelId channelId) => _actor.UnregisterChannelAsync(channelId);

    public Task<OutboundMessage> RouteInboundAsync(InboundMessage message, CapabilityToken token) =>
        _actor.RouteInboundAsync(message, token);

    public Task<IReadOnlyList<ChannelConfig>> GetChannelsAsync() => _actor.GetChannelsAsync();
    public Task SetRoutingRuleAsync(string pattern, string agentName) => _actor.SetRoutingRuleAsync(pattern, agentName);
}
