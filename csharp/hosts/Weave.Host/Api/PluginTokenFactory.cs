using Weave.Security.Tokens;

namespace Weave.Silo.Api;

internal static class PluginTokenFactory
{
    private static readonly TimeSpan ApiTokenLifetime = TimeSpan.FromMinutes(1);

    public static CapabilityTokenSource MintInvoke(
        ICapabilityTokenService tokenService,
        string pluginName,
        CancellationToken parentCt) =>
        tokenService.MintLinked(
            new CapabilityTokenRequest
            {
                WorkspaceId = "silo",
                IssuedTo = "api/plugins",
                Grants = [$"plugin:invoke:{pluginName}"],
                Lifetime = ApiTokenLifetime
            },
            parentCt);
}
