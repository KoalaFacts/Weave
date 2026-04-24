using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

public interface IChannelGatewayActor
{
    Task RegisterChannelAsync(ChannelConfig config);
    Task UnregisterChannelAsync(ChannelId channelId);
    Task<OutboundMessage> RouteInboundAsync(InboundMessage message);
    Task<IReadOnlyList<ChannelConfig>> GetChannelsAsync();
    Task SetRoutingRuleAsync(string pattern, string agentName);
}
