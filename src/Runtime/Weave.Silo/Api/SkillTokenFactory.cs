using Weave.Security.Tokens;

namespace Weave.Silo.Api;

internal static class SkillTokenFactory
{
    private static readonly TimeSpan ApiTokenLifetime = TimeSpan.FromMinutes(1);

    public static CapabilityToken MintRead(ICapabilityTokenService tokenService, string workspaceId) =>
        Mint(tokenService, workspaceId, "skill:read");

    public static CapabilityToken MintWrite(ICapabilityTokenService tokenService, string workspaceId) =>
        Mint(tokenService, workspaceId, "skill:write");

    private static CapabilityToken Mint(ICapabilityTokenService tokenService, string workspaceId, string grant) =>
        tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = $"api/{workspaceId}",
            Grants = [grant],
            Lifetime = ApiTokenLifetime
        });
}
