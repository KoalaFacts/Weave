using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Agents.Channels;

public interface IChannelGatewayActor
{
    Task RegisterChannelAsync(ChannelConfig config);
    Task UnregisterChannelAsync(ChannelId channelId);
    Task<OutboundMessage> RouteInboundAsync(InboundMessage message, CapabilityToken token);
    Task<IReadOnlyList<ChannelConfig>> GetChannelsAsync();
    Task SetRoutingRuleAsync(string pattern, string agentName);
}
