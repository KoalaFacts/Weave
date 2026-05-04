using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Channels;

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
