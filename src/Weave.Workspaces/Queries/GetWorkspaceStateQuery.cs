using Orleans;
using Weave.Shared.Cqrs;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Queries;

public sealed record GetWorkspaceStateQuery(string WorkspaceId);

public sealed class GetWorkspaceStateHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetWorkspaceStateQuery, WorkspaceState>
{
    public async Task<WorkspaceState> HandleAsync(GetWorkspaceStateQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IWorkspaceGrain>(query.WorkspaceId);
        return await grain.GetStateAsync();
    }
}
