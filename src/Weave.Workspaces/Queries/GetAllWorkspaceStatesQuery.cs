using Weave.Shared.Cqrs;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Queries;

public sealed record GetAllWorkspaceStatesQuery();

public sealed class GetAllWorkspaceStatesHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetAllWorkspaceStatesQuery, IReadOnlyList<WorkspaceState>>
{
    public async Task<IReadOnlyList<WorkspaceState>> HandleAsync(GetAllWorkspaceStatesQuery query, CancellationToken ct)
    {
        var registry = grainFactory.GetGrain<IWorkspaceRegistryGrain>("active");
        var workspaceIds = await registry.GetWorkspaceIdsAsync();
        var states = new List<WorkspaceState>(workspaceIds.Count);

        foreach (var workspaceId in workspaceIds)
        {
            ct.ThrowIfCancellationRequested();
            var grain = grainFactory.GetGrain<IWorkspaceGrain>(workspaceId);
            states.Add(await grain.GetStateAsync());
        }

        return [.. states.OrderBy(static s => s.WorkspaceId.ToString(), StringComparer.Ordinal)];
    }
}
