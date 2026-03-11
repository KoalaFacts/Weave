using Orleans;
using Weave.Shared.Cqrs;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Commands;

public sealed record StartWorkspaceCommand(string WorkspaceId, WorkspaceManifest Manifest);

public sealed class StartWorkspaceHandler(IGrainFactory grainFactory)
    : ICommandHandler<StartWorkspaceCommand, WorkspaceState>
{
    public async Task<WorkspaceState> HandleAsync(StartWorkspaceCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IWorkspaceGrain>(command.WorkspaceId);
        return await grain.StartAsync(command.Manifest);
    }
}
