using Weave.Plugins;
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
        IInstallationDiagnostics diagnostics,
        CancellationToken ct)
    {
        var denial = await authority.DenialAsync(context, workspaceId,
            PluginInstallationAuthority.InstallationReadGrant);
        if (denial is not null)
            return denial;

        ct.ThrowIfCancellationRequested();
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
        var state = await workspace.GetStateAsync();
        if (!WorkspaceReadPresence.Exists(state))
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
            result.Add(Project(installation, "dapr_tools", connected, diagnostics));
        }
        foreach (var installation in state.McpToolInstallations)
        {
            if (!HasValidIdentity(installation.Id, installation.PluginName, workspaceId)
                || !ids.Add(installation.Id))
                return ResultExtensions.Conflict("Stored installation identity is inconsistent.");
            result.Add(Project(installation, "mcp_tools", connected, diagnostics));
        }

        result.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return Results.Ok(result);
    }

    private static bool HasValidIdentity(string id, string pluginName, string workspaceId) =>
        !string.IsNullOrWhiteSpace(pluginName)
        && string.Equals(id, $"{workspaceId}/{pluginName}", StringComparison.Ordinal);

    private static PluginInstallationResponse Project(PluginInstallation installation, string type,
        IReadOnlyList<PluginStatus> connected, IInstallationDiagnostics diagnostics)
    {
        var id = installation.Id;
        var desiredEnabled = installation.DesiredEnabled;
        var runtimeConnected = connected.Any(item => string.Equals(item.Name, id, StringComparison.Ordinal)
            && string.Equals(item.Type, type, StringComparison.Ordinal));
        var observation = diagnostics.Get(id);
        var failure = observation?.Failure ?? InstallationFailureCode.None;
        var condition = (desiredEnabled, runtimeConnected, failure) switch
        {
            (false, true, _) => "inconsistent",
            (false, false, _) => "disabled",
            (true, true, _) => "ready",
            (true, false, not InstallationFailureCode.None) => "blocked",
            _ => "unobserved"
        };
        var reasonCode = condition switch
        {
            "inconsistent" => "still_connected",
            _ => ReasonCode(failure)
        };
        var probe = desiredEnabled ? diagnostics.GetProbe(installation) : null;
        var probeCondition = !desiredEnabled ? "not_applicable" : probe is null ? "unobserved"
            : probe.Failure == InstallationFailureCode.None ? "responding" : "blocked";
        return new PluginInstallationResponse(id, installation.PluginName, type,
            installation.DefinitionRevision, desiredEnabled,
            runtimeConnected, condition, reasonCode, observation?.CheckedAt,
            installation.RequestedPermissions.ToArray(), installation.GrantedPermissions.ToArray(),
            installation.CredentialReferences.Count > 0, probeCondition,
            probe is null ? null : ReasonCode(probe.Failure), probe?.CheckedAt);
    }

    private static string? ReasonCode(InstallationFailureCode failure) => failure switch
    {
        InstallationFailureCode.None => null,
        InstallationFailureCode.StoredConfigurationInvalid => "stored_configuration_invalid",
        InstallationFailureCode.UnsupportedAuthorityState => "unsupported_authority_state",
        InstallationFailureCode.DefinitionRevisionChanged => "definition_revision_changed",
        InstallationFailureCode.ContractUnpinned => "contract_unpinned",
        InstallationFailureCode.InvalidConfiguration => "invalid_configuration",
        InstallationFailureCode.RegistrationConflict => "registration_conflict",
        InstallationFailureCode.UnsupportedPluginType => "unsupported_plugin_type",
        InstallationFailureCode.PeerUnavailable => "peer_unavailable",
        InstallationFailureCode.ContractRejected => "contract_rejected",
        _ => "connection_failed"
    };
}
