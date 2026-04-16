using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Grains;

public interface IChannelGatewayGrain : IGrainWithStringKey
{
    Task RegisterChannelAsync(ChannelConfig config);
    Task UnregisterChannelAsync(ChannelId channelId);
    Task<OutboundMessage> RouteInboundAsync(InboundMessage message);
    Task<IReadOnlyList<ChannelConfig>> GetChannelsAsync();
    Task SetRoutingRuleAsync(string pattern, string agentName);
}
