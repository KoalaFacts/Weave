using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record CompleteAgentTaskCommand(
    WorkspaceId WorkspaceId,
    string AgentName,
    AgentTaskId TaskId,
    bool Success,
    ProofOfWork Proof);

public sealed class CompleteAgentTaskHandler(IVirtualActorProvider actors)
    : ICommandHandler<CompleteAgentTaskCommand, AgentTaskInfo>
{
    public async Task<AgentTaskInfo> HandleAsync(CompleteAgentTaskCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IAgentActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
        await actor.CompleteTaskAsync(command.TaskId, command.Success, command.Proof);
        var state = await actor.GetStateAsync();
        return state.ActiveTasks.First(t => t.TaskId == command.TaskId);
    }
}
