using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

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
