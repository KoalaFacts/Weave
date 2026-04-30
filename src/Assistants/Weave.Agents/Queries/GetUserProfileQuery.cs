using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetUserProfileQuery(WorkspaceId WorkspaceId, string UserId);

public sealed class GetUserProfileHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetUserProfileQuery, UserProfileState>
{
    public async Task<UserProfileState> HandleAsync(GetUserProfileQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<IUserModelActor>(VirtualActorId.Combine(query.WorkspaceId, query.UserId));
        return await actor.GetProfileAsync();
    }
}
