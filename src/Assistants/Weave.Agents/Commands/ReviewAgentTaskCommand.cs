using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Commands;

public sealed record ReviewAgentTaskCommand(
    WorkspaceId WorkspaceId,
    string AgentName,
    AgentTaskId TaskId,
    bool Accepted,
    string? Feedback = null);

public sealed class ReviewAgentTaskHandler(IVirtualActorProvider actors)
    : ICommandHandler<ReviewAgentTaskCommand, AgentTaskInfo>
{
    public async Task<AgentTaskInfo> HandleAsync(ReviewAgentTaskCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IAgentActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
        await actor.ReviewTaskAsync(command.TaskId, command.Accepted, command.Feedback);
        var state = await actor.GetStateAsync();
        return state.ActiveTasks.First(t => t.TaskId == command.TaskId);
    }
}
