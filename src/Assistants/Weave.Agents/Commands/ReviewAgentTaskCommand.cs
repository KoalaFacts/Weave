using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record ReviewAgentTaskCommand(
    WorkspaceId WorkspaceId,
    string AgentName,
    AgentTaskId TaskId,
    bool Accepted,
    string? Feedback = null);

public sealed class ReviewAgentTaskHandler(IGrainFactory grainFactory)
    : ICommandHandler<ReviewAgentTaskCommand, AgentTaskInfo>
{
    public async Task<AgentTaskInfo> HandleAsync(ReviewAgentTaskCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IAgentGrain>($"{command.WorkspaceId}/{command.AgentName}");
        await grain.ReviewTaskAsync(command.TaskId, command.Accepted, command.Feedback);
        var state = await grain.GetStateAsync();
        return state.ActiveTasks.First(t => t.TaskId == command.TaskId);
    }
}
