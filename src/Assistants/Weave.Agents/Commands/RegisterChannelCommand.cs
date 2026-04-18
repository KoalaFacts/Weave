using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record RegisterChannelCommand(WorkspaceId WorkspaceId, ChannelConfig Config);

public sealed class RegisterChannelHandler(IGrainFactory grainFactory)
    : ICommandHandler<RegisterChannelCommand, bool>
{
    public async Task<bool> HandleAsync(RegisterChannelCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IChannelGatewayGrain>(command.WorkspaceId.ToString());
        await grain.RegisterChannelAsync(command.Config);
        return true;
    }
}
