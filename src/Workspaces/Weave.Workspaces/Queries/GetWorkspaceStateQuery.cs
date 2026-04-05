using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Queries;

public sealed record GetWorkspaceStateQuery(WorkspaceId WorkspaceId);

public sealed class GetWorkspaceStateHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetWorkspaceStateQuery, WorkspaceState?>
{
    public async Task<WorkspaceState?> HandleAsync(GetWorkspaceStateQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IWorkspaceGrain>(query.WorkspaceId.ToString());
        var state = await grain.GetStateAsync();
        return state.WorkspaceId.IsEmpty ? null : state;
    }
}
