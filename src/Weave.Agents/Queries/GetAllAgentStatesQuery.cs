using Orleans;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;

namespace Weave.Agents.Queries;

public sealed record GetAllAgentStatesQuery(string WorkspaceId);

public sealed class GetAllAgentStatesHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetAllAgentStatesQuery, IReadOnlyList<AgentState>>
{
    public async Task<IReadOnlyList<AgentState>> HandleAsync(GetAllAgentStatesQuery query, CancellationToken ct)
    {
        var supervisor = grainFactory.GetGrain<IAgentSupervisorGrain>(query.WorkspaceId);
        return await supervisor.GetAllAgentStatesAsync();
    }
}
