using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Agents.Queries;

public sealed record GetUserProfileQuery(WorkspaceId WorkspaceId, string UserId);

public sealed class GetUserProfileHandler(IGrainFactory grainFactory)
    : IQueryHandler<GetUserProfileQuery, UserProfileState>
{
    public async Task<UserProfileState> HandleAsync(GetUserProfileQuery query, CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IUserModelGrain>($"{query.WorkspaceId}/{query.UserId}");
        return await grain.GetProfileAsync();
    }
}
