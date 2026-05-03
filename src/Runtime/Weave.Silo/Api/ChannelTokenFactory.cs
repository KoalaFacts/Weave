using Weave.Security.Tokens;

namespace Weave.Silo.Api;

internal static class ChannelTokenFactory
{
    private static readonly TimeSpan ApiTokenLifetime = TimeSpan.FromMinutes(1);

    public static CapabilityToken MintInbound(ICapabilityTokenService tokenService, string workspaceId, string channelId) =>
        tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = $"api/{workspaceId}",
            Grants = [$"channel:receive:{channelId}", $"channel:send:{channelId}"],
            Lifetime = ApiTokenLifetime
        });
}
