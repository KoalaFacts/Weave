using Weave.Security.Tokens;

namespace Weave.Silo.Api;

internal static class UserTokenFactory
{
    private static readonly TimeSpan ApiTokenLifetime = TimeSpan.FromMinutes(1);

    public static CapabilityTokenSource MintRead(ICapabilityTokenService tokenService, string workspaceId, string userId, CancellationToken parentCt) =>
        Mint(tokenService, workspaceId, $"user:read:{userId}", parentCt);

    public static CapabilityTokenSource MintWrite(ICapabilityTokenService tokenService, string workspaceId, string userId, CancellationToken parentCt) =>
        Mint(tokenService, workspaceId, $"user:write:{userId}", parentCt);

    private static CapabilityTokenSource Mint(ICapabilityTokenService tokenService, string workspaceId, string grant, CancellationToken parentCt) =>
        tokenService.MintLinked(
            new CapabilityTokenRequest
            {
                WorkspaceId = workspaceId,
                IssuedTo = $"api/{workspaceId}",
                Grants = [grant],
                Lifetime = ApiTokenLifetime
            },
            parentCt);
}
