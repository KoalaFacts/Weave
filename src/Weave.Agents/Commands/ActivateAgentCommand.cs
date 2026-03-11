using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Commands;

public sealed record ActivateAgentCommand(WorkspaceId WorkspaceId, string AgentName, AgentDefinition Definition);

public sealed class ActivateAgentHandler(IGrainFactory grainFactory)
    : ICommandHandler<ActivateAgentCommand, AgentState>
{
    public async Task<AgentState> HandleAsync(ActivateAgentCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{command.WorkspaceId}/{command.AgentName}");
        return await grain.ActivateAgentAsync(command.WorkspaceId, command.Definition);
    }
}
