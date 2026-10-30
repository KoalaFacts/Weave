using Weave.Security.Tokens;

namespace Weave.Silo.Api;

internal static class ChannelTokenFactory
{
    private static readonly TimeSpan ApiTokenLifetime = TimeSpan.FromMinutes(1);

    public static CapabilityTokenSource MintInbound(
        ICapabilityTokenService tokenService,
        string workspaceId,
        string channelId,
        CancellationToken parentCt) =>
        tokenService.MintLinked(
            new CapabilityTokenRequest
            {
                WorkspaceId = workspaceId,
                IssuedTo = $"api/{workspaceId}",
                Grants = [$"channel:receive:{channelId}", $"channel:send:{channelId}"],
                Lifetime = ApiTokenLifetime
            },
            parentCt);
}
