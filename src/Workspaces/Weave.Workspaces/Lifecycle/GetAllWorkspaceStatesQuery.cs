using Weave.Shared.Cqrs;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Lifecycle;

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
