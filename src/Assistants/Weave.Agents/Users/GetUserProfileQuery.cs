using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Users;

public sealed record GetUserProfileQuery(WorkspaceId WorkspaceId, string UserId, CapabilityToken Token);

public sealed class GetUserProfileHandler(IVirtualActorProvider actors)
    : IQueryHandler<GetUserProfileQuery, UserProfileState>
{
    public async Task<UserProfileState> HandleAsync(GetUserProfileQuery query, CancellationToken ct)
    {
        var actor = actors.GetActor<IUserModelActor>(VirtualActorId.Combine(query.WorkspaceId, query.UserId));
        return await actor.GetProfileAsync(query.Token);
    }
}
