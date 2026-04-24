using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;

namespace Weave.Agents.Commands;

public sealed record SubmitAgentTaskCommand(WorkspaceId WorkspaceId, string AgentName, string Description);

public sealed class SubmitAgentTaskHandler(IVirtualActorProvider actors)
    : ICommandHandler<SubmitAgentTaskCommand, AgentTaskInfo>
{
    public async Task<AgentTaskInfo> HandleAsync(SubmitAgentTaskCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IAgentActor>(VirtualActorId.Combine(command.WorkspaceId, command.AgentName));
        return await actor.SubmitTaskAsync(command.Description);
    }
}
