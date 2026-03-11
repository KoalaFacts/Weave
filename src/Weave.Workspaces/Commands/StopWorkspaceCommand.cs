using Orleans;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Grains;

namespace Weave.Workspaces.Commands;

public sealed record StopWorkspaceCommand(WorkspaceId WorkspaceId);

public sealed class StopWorkspaceHandler(IGrainFactory grainFactory)
    : ICommandHandler<StopWorkspaceCommand, bool>
{
    public async Task<bool> HandleAsync(StopWorkspaceCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IWorkspaceGrain>(command.WorkspaceId.ToString());
        await grain.StopAsync();
        return true;
    }
}
