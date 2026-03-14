using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record CompleteAgentTaskCommand(WorkspaceId WorkspaceId, string AgentName, AgentTaskId TaskId, bool Success);

public sealed class CompleteAgentTaskHandler(IGrainFactory grainFactory)
    : ICommandHandler<CompleteAgentTaskCommand, AgentTaskInfo>
{
    public async Task<AgentTaskInfo> HandleAsync(CompleteAgentTaskCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{command.WorkspaceId}/{command.AgentName}");
        await grain.CompleteTaskAsync(command.TaskId, command.Success);
        var state = await grain.GetStateAsync();
        return state.ActiveTasks.First(t => t.TaskId == command.TaskId);
    }
}
