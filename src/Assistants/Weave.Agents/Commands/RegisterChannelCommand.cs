using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Commands;

public sealed record RegisterChannelCommand(WorkspaceId WorkspaceId, ChannelConfig Config);

public sealed class RegisterChannelHandler(IVirtualActorProvider actors)
    : ICommandHandler<RegisterChannelCommand, bool>
{
    public async Task<bool> HandleAsync(RegisterChannelCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IChannelGatewayActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        await actor.RegisterChannelAsync(command.Config);
        return true;
    }
}
