using Weave.Security.Tokens;
using Weave.Silo.Invocations;

namespace Weave.Silo.Plugins;

internal interface IPluginInstallationAuthority
{
    Task<IResult?> DenialAsync(HttpContext context, string workspaceId, params string[] grants);
}

internal sealed class PluginInstallationAuthority(
    ICapabilityTokenService tokens,
    ICapabilityAuthorizer authorizer) : IPluginInstallationAuthority
{
    public const string CreateWorkspaceGrant = "workspace:create";
    public const string StopWorkspaceGrant = "workspace:stop";
    public const string InstallationReadGrant = "plugin:installations:read";
    public const string McpInstallGrant = "plugin:mcp_tools:install";
    public const string McpEnableGrant = "plugin:mcp_tools:enable";
    public const string McpDisableGrant = "plugin:mcp_tools:disable";
    public const string DaprInstallGrant = "plugin:dapr_tools:install";
    public const string DaprEnableGrant = "plugin:dapr_tools:enable";
    public const string DaprDisableGrant = "plugin:dapr_tools:disable";

    public async Task<IResult?> DenialAsync(HttpContext context, string workspaceId, params string[] grants)
    {
        if (!InvocationHttp.TryReadCapability(context, tokens, out var token, out var failure))
            return failure;
        if (!InvocationHttp.IsRouteSegment(workspaceId))
            return InvocationHttp.Error(400, "invalid-route-identity");
        try
        {
            foreach (var grant in grants)
                await authorizer.AuthorizeAsync(token, grant, workspaceId, nameof(PluginInstallationAuthority));
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }
}
