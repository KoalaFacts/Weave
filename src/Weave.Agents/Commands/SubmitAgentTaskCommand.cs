using Orleans;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record SubmitAgentTaskCommand(WorkspaceId WorkspaceId, string AgentName, string Description);

public sealed class SubmitAgentTaskHandler(IGrainFactory grainFactory)
    : ICommandHandler<SubmitAgentTaskCommand, AgentTaskInfo>
{
    public async Task<AgentTaskInfo> HandleAsync(SubmitAgentTaskCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{command.WorkspaceId}/{command.AgentName}");
        return await grain.SubmitTaskAsync(command.Description);
    }
}
