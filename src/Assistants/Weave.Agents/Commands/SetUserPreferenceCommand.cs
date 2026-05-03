using Weave.Agents.Actors;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

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
