using Weave.Security.Tokens;
using Weave.Silo.Invocations;

namespace Weave.Silo.Plugins;

internal interface IMcpInstallationAuthority
{
    Task<IResult?> DenialAsync(HttpContext context, string workspaceId, string grant, string? secondGrant = null);
}

internal sealed class McpInstallationAuthority(
    ICapabilityTokenService tokens,
    ICapabilityAuthorizer authorizer) : IMcpInstallationAuthority
{
    public const string InstallGrant = "plugin:mcp_tools:install";
    public const string CreateWorkspaceGrant = "workspace:create";
    public const string StopWorkspaceGrant = "workspace:stop";
    public const string EnableGrant = "plugin:mcp_tools:enable";
    public const string DisableGrant = "plugin:mcp_tools:disable";

    public async Task<IResult?> DenialAsync(HttpContext context, string workspaceId, string grant,
        string? secondGrant = null)
    {
        if (!InvocationHttp.TryReadCapability(context, tokens, out var token, out var failure))
            return failure;
        if (!InvocationHttp.IsRouteSegment(workspaceId))
            return InvocationHttp.Error(400, "invalid-route-identity");
        try
        {
            await authorizer.AuthorizeAsync(token, grant, workspaceId, nameof(McpInstallationAuthority));
            if (secondGrant is not null)
                await authorizer.AuthorizeAsync(token, secondGrant, workspaceId, nameof(McpInstallationAuthority));
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }
}
