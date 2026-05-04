using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Users;

public sealed record SetUserPreferenceCommand(
    WorkspaceId WorkspaceId,
    string UserId,
    string Key,
    string Value,
    CapabilityToken Token);

public sealed class SetUserPreferenceHandler(IVirtualActorProvider actors)
    : ICommandHandler<SetUserPreferenceCommand, bool>
{
    public async Task<bool> HandleAsync(SetUserPreferenceCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IUserModelActor>(VirtualActorId.Combine(command.WorkspaceId, command.UserId));
        await actor.SetPreferenceAsync(command.Key, command.Value, command.Token);
        return true;
    }
}
