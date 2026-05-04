using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Channels;

public sealed record RouteInboundMessageCommand(
    WorkspaceId WorkspaceId,
    InboundMessage Message,
    CapabilityToken Token);

public sealed class RouteInboundMessageHandler(IVirtualActorProvider actors)
    : ICommandHandler<RouteInboundMessageCommand, OutboundMessage>
{
    public async Task<OutboundMessage> HandleAsync(RouteInboundMessageCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IChannelGatewayActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        return await actor.RouteInboundAsync(command.Message, command.Token);
    }
}
