using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Memory;

public sealed record ArchiveEpisodeCommand(WorkspaceId WorkspaceId, EpisodeId EpisodeId);

public sealed class ArchiveEpisodeHandler(IVirtualActorProvider actors)
    : ICommandHandler<ArchiveEpisodeCommand, Episode>
{
    public async Task<Episode> HandleAsync(ArchiveEpisodeCommand command, CancellationToken ct)
    {
        var actor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        return await actor.ArchiveEpisodeAsync(command.EpisodeId)
            ?? throw new KeyNotFoundException($"Episode '{command.EpisodeId}' not found.");
    }
}
