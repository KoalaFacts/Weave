using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record RouteInboundMessageCommand(WorkspaceId WorkspaceId, InboundMessage Message);

public sealed class RouteInboundMessageHandler(IGrainFactory grainFactory)
    : ICommandHandler<RouteInboundMessageCommand, OutboundMessage>
{
    public async Task<OutboundMessage> HandleAsync(RouteInboundMessageCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IChannelGatewayGrain>(command.WorkspaceId.ToString());
        return await grain.RouteInboundAsync(command.Message);
    }
}
