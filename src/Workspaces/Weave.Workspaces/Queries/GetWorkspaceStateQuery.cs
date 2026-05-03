using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Actors;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Queries;

public sealed record GetWorkspaceStateQuery(WorkspaceId WorkspaceId);

public sealed class GetWorkspaceStateHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetWorkspaceStateQuery, WorkspaceState>
{
    public async Task<WorkspaceState> HandleAsync(GetWorkspaceStateQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(query.WorkspaceId.ToString()));
        return await actor.GetStateAsync();
    }
}
