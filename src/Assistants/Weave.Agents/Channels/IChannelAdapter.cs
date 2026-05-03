using Weave.Agents.Models;

namespace Weave.Agents.Channels;

public interface IChannelAdapter
{
    ChannelType Type { get; }
    Task SendAsync(OutboundMessage message, ChannelConfig config, CancellationToken ct);
    Task<bool> ValidateConfigAsync(Dictionary<string, string> config, CancellationToken ct);
}
