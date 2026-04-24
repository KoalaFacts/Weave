using Weave.Shared.Cqrs;
using Weave.Shared.VirtualActors;
using Weave.Workspaces.Actors;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Queries;

public sealed record GetAllWorkspaceStatesQuery();

public sealed class GetAllWorkspaceStatesHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetAllWorkspaceStatesQuery, IReadOnlyList<WorkspaceState>>
{
    public async Task<IReadOnlyList<WorkspaceState>> HandleAsync(GetAllWorkspaceStatesQuery query, CancellationToken ct)
    {
        var registry = actors.GetActor<IWorkspaceRegistryActor>(VirtualActorId.From("active"));
        var workspaceIds = await registry.GetWorkspaceIdsAsync();
        var states = new List<WorkspaceState>(workspaceIds.Count);

        foreach (var workspaceId in workspaceIds)
        {
            ct.ThrowIfCancellationRequested();
            var actor = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
            states.Add(await actor.GetStateAsync());
        }

        return [.. states.OrderBy(static s => s.WorkspaceId.ToString(), StringComparer.Ordinal)];
    }
}
