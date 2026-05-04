using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

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
