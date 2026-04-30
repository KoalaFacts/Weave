using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Silo.VirtualActors;

public sealed class ChannelGatewayActorGrain : Grain, IChannelGatewayActorGrain
{
    private readonly ChannelGatewayActor _actor;

    public ChannelGatewayActorGrain(
        IVirtualActorProvider actors,
        IEventBus eventBus,
        ILogger<ChannelGatewayActor> logger,
        [PersistentState("channel-gateway", "Default")] IPersistentState<ChannelGatewayState> state)
    {
        _actor = new ChannelGatewayActor(actors, eventBus, logger,
            new OrleansActorState<ChannelGatewayState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RegisterChannelAsync(ChannelConfig config) => _actor.RegisterChannelAsync(config);
    public Task UnregisterChannelAsync(ChannelId channelId) => _actor.UnregisterChannelAsync(channelId);

    public Task<OutboundMessage> RouteInboundAsync(InboundMessage message) =>
        _actor.RouteInboundAsync(message);

    public Task<IReadOnlyList<ChannelConfig>> GetChannelsAsync() => _actor.GetChannelsAsync();
    public Task SetRoutingRuleAsync(string pattern, string agentName) => _actor.SetRoutingRuleAsync(pattern, agentName);
}
