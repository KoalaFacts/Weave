using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

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
