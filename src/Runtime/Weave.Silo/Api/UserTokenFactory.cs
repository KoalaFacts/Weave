using Weave.Security.Tokens;

namespace Weave.Silo.Api;

internal static class UserTokenFactory
{
    private static readonly TimeSpan ApiTokenLifetime = TimeSpan.FromMinutes(1);

    public static CapabilityToken MintRead(ICapabilityTokenService tokenService, string workspaceId, string userId) =>
        Mint(tokenService, workspaceId, $"user:read:{userId}");

    public static CapabilityToken MintWrite(ICapabilityTokenService tokenService, string workspaceId, string userId) =>
        Mint(tokenService, workspaceId, $"user:write:{userId}");

    private static CapabilityToken Mint(ICapabilityTokenService tokenService, string workspaceId, string grant) =>
        tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = $"api/{workspaceId}",
            Grants = [grant],
            Lifetime = ApiTokenLifetime
        });
}
