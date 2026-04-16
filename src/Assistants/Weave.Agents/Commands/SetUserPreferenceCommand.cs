using Weave.Agents.Grains;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Commands;

public sealed record SetUserPreferenceCommand(WorkspaceId WorkspaceId, string UserId, string Key, string Value);

public sealed class SetUserPreferenceHandler(IGrainFactory grainFactory)
    : ICommandHandler<SetUserPreferenceCommand, bool>
{
    public async Task<bool> HandleAsync(SetUserPreferenceCommand command, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IUserModelGrain>($"{command.WorkspaceId}/{command.UserId}");
        await grain.SetPreferenceAsync(command.Key, command.Value);
        return true;
    }
}
