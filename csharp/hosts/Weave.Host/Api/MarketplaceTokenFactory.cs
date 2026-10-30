using Weave.Security.Tokens;

namespace Weave.Silo.Api;

internal static class MarketplaceTokenFactory
{
    private static readonly TimeSpan ApiTokenLifetime = TimeSpan.FromMinutes(1);

    public static CapabilityTokenSource MintInstall(
        ICapabilityTokenService tokenService,
        CancellationToken parentCt) =>
        tokenService.MintLinked(
            new CapabilityTokenRequest
            {
                WorkspaceId = "silo",
                IssuedTo = "api/marketplace",
                Grants = ["marketplace:install"],
                Lifetime = ApiTokenLifetime
            },
            parentCt);
}
