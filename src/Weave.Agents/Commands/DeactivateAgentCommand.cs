using Orleans;
using Weave.Agents.Grains;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record DeactivateAgentCommand(WorkspaceId WorkspaceId, string AgentName);

public sealed class DeactivateAgentHandler(IGrainFactory grainFactory)
    : ICommandHandler<DeactivateAgentCommand, bool>
{
    public async Task<bool> HandleAsync(DeactivateAgentCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{command.WorkspaceId}/{command.AgentName}");
        await grain.DeactivateAsync();
        return true;
    }
}
