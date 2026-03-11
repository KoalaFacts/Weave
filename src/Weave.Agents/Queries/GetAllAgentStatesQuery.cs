using Orleans;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetAllAgentStatesQuery(WorkspaceId WorkspaceId);

public sealed class GetAllAgentStatesHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetAllAgentStatesQuery, IReadOnlyList<AgentState>>
{
    public async Task<IReadOnlyList<AgentState>> HandleAsync(GetAllAgentStatesQuery query, CancellationToken ct)
    {
        var supervisor = grainFactory.GetGrain<IAgentSupervisorGrain>(query.WorkspaceId.ToString());
        return await supervisor.GetAllAgentStatesAsync();
    }
}
