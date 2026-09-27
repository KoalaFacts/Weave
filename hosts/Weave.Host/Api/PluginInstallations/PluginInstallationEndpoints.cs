using Weave.Silo.Plugins;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.Api;

public static class PluginInstallationEndpoints
{
    public static RouteGroupBuilder MapPluginInstallationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/plugins/installations")
            .WithTags("Plugins");
        group.MapGet("/{workspaceId}", GetInstallationsAsync)
            .WithDescription("List persisted tool plugin installations for one workspace.")
            .Produces<List<PluginInstallationResponse>>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409);
        return group;
    }

    private static async Task<IResult> GetInstallationsAsync(
        string workspaceId,
        HttpContext context,
        IPluginInstallationAuthority authority,
        IVirtualActorProvider actors,
        IPluginRegistry plugins,
        CancellationToken ct)
    {
        var denial = await authority.DenialAsync(context, workspaceId,
            PluginInstallationAuthority.InstallationReadGrant);
        if (denial is not null)
            return denial;

        ct.ThrowIfCancellationRequested();
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
        var state = await workspace.GetStateAsync();
        if (state.WorkspaceId.IsEmpty)
            return ResultExtensions.NotFound($"Workspace '{workspaceId}' not found.");
        if (!string.Equals(state.WorkspaceId.ToString(), workspaceId, StringComparison.Ordinal))
            return ResultExtensions.Conflict("Stored workspace identity does not match the requested workspace.");

        var connected = plugins.GetAll().Where(static item => item.IsConnected).ToList();
        var result = new List<PluginInstallationResponse>(
            state.DaprToolInstallations.Count + state.McpToolInstallations.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installation in state.DaprToolInstallations)
        {
            if (!HasValidIdentity(installation.Id, installation.PluginName, workspaceId)
                || !ids.Add(installation.Id))
                return ResultExtensions.Conflict("Stored installation identity is inconsistent.");
            result.Add(new PluginInstallationResponse(installation.Id, installation.PluginName,
                "dapr_tools", installation.DesiredEnabled,
                connected.Any(item => string.Equals(item.Name, installation.Id, StringComparison.Ordinal)
                    && string.Equals(item.Type, "dapr_tools", StringComparison.Ordinal))));
        }
        foreach (var installation in state.McpToolInstallations)
        {
            if (!HasValidIdentity(installation.Id, installation.PluginName, workspaceId)
                || !ids.Add(installation.Id))
                return ResultExtensions.Conflict("Stored installation identity is inconsistent.");
            result.Add(new PluginInstallationResponse(installation.Id, installation.PluginName,
                "mcp_tools", installation.DesiredEnabled,
                connected.Any(item => string.Equals(item.Name, installation.Id, StringComparison.Ordinal)
                    && string.Equals(item.Type, "mcp_tools", StringComparison.Ordinal))));
        }

        result.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return Results.Ok(result);
    }

    private static bool HasValidIdentity(string id, string pluginName, string workspaceId) =>
        !string.IsNullOrWhiteSpace(pluginName)
        && string.Equals(id, $"{workspaceId}/{pluginName}", StringComparison.Ordinal);
}
