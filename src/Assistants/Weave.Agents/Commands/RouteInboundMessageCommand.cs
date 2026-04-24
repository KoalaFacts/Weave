using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Commands;

public sealed record RouteInboundMessageCommand(WorkspaceId WorkspaceId, InboundMessage Message);

public sealed class RouteInboundMessageHandler(IVirtualActorProvider actors)
    : ICommandHandler<RouteInboundMessageCommand, OutboundMessage>
{
    public async Task<OutboundMessage> HandleAsync(RouteInboundMessageCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IChannelGatewayActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        return await actor.RouteInboundAsync(command.Message);
    }
}
