using System.Text;
using System.Text.Json;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;
using Weave.Silo.Management;
using Weave.Silo.Plugins;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
namespace Weave.Silo.Api;

public static class WorkspaceEndpoints
{
    public static RouteGroupBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces")
            .WithTags("Workspaces");

        group.MapGet("/", GetAllWorkspacesAsync)
            .WithDescription("List all workspaces.")
            .Produces<IEnumerable<WorkspaceResponse>>();
        group.MapGet("/{workspaceId}", GetWorkspaceStateAsync)
            .WithDescription("Get the current state of a workspace.")
            .Produces<WorkspaceResponse>()
            .ProducesProblem(404);
        group.MapPost("/", StartWorkspaceAsync)
            .WithDescription("Start a new workspace from a manifest.")
            .Produces<WorkspaceResponse>(201)
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/validate", ValidateManifestAsync)
            .WithDescription("Parse and structurally validate a workspace manifest. Returns the per-error list inside a 200 response; only parse failures return 400.")
            .Produces<ValidateWorkspaceManifestResult>()
            .ProducesValidationProblem();
        group.MapDelete("/{workspaceId}", StopWorkspaceAsync)
            .WithDescription("Stop a workspace.")
            .Produces(204)
            .ProducesProblem(409);

        return group;
    }

    // --- GET endpoints ---

    private static async Task<IResult> GetAllWorkspacesAsync(
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var states = await dispatcher.DispatchAsync<GetAllWorkspaceStatesQuery, IReadOnlyList<WorkspaceState>>(
            new GetAllWorkspaceStatesQuery(),
            ct);
        return Results.Ok(states.Select(WorkspaceResponse.FromState));
    }

    private static async Task<IResult> GetWorkspaceStateAsync(
        string workspaceId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetWorkspaceStateQuery(WorkspaceId.From(workspaceId));
        var state = await dispatcher.DispatchAsync<GetWorkspaceStateQuery, WorkspaceState>(query, ct);
        if (state.WorkspaceId.IsEmpty)
            return ResultExtensions.NotFound($"Workspace '{workspaceId}' not found.");

        return Results.Ok(WorkspaceResponse.FromState(state));
    }

    // --- POST/DELETE endpoints ---

    private static async Task<IResult> StartWorkspaceAsync(
        StartWorkspaceRequest request,
        ICommandDispatcher dispatcher,
        HttpContext context,
        IPluginInstallationAuthority authority,
        ManagementAdmission admission,
        CancellationToken ct)
    {
        var installationGrants = new List<string>();
        if (request.Manifest.Plugins.Values.Any(plugin =>
            string.Equals(plugin.Type, "mcp_tools", StringComparison.OrdinalIgnoreCase)))
            installationGrants.Add(PluginInstallationAuthority.McpInstallGrant);
        if (request.Manifest.Plugins.Values.Any(plugin =>
            string.Equals(plugin.Type, "dapr_tools", StringComparison.OrdinalIgnoreCase)))
            installationGrants.Add(PluginInstallationAuthority.DaprInstallGrant);
        installationGrants.Insert(0, PluginInstallationAuthority.CreateWorkspaceGrant);
        var denial = await authority.DenialAsync(context, "silo", installationGrants.ToArray());
        if (denial is not null)
            return denial;
        var errors = ValidateStartWorkspace(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var workspaceId = WorkspaceId.New();
        var admitted = admission.Admit(context, "silo", "workspace:create", workspaceId.ToString(),
            installationGrants,
            JsonSerializer.SerializeToUtf8Bytes(request, SiloApiJsonContext.Default.StartWorkspaceRequest),
            out var managementId);
        if (admitted is not null)
            return admitted;

        try
        {
            var command = new StartWorkspaceCommand(workspaceId, request.Manifest);
            var state = await dispatcher.DispatchAsync<StartWorkspaceCommand, WorkspaceState>(command, ct);
            return admission.Confirm(managementId,
                Results.Created($"/api/workspaces/{workspaceId}", WorkspaceResponse.FromState(state)));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> ValidateManifestAsync(
        ValidateWorkspaceManifestRequest request,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ManifestJson))
        {
            return ResultExtensions.ValidationFailed(new Dictionary<string, string[]>
            {
                ["manifestJson"] = ["Manifest JSON is required."]
            });
        }

        try
        {
            var query = new ValidateWorkspaceManifestQuery(request.ManifestJson);
            var result = await dispatcher.DispatchAsync<ValidateWorkspaceManifestQuery, ValidateWorkspaceManifestResult>(query, ct);
            return Results.Ok(result);
        }
        catch (JsonException ex)
        {
            return ResultExtensions.ValidationFailed(new Dictionary<string, string[]>
            {
                ["manifestJson"] = [$"Manifest is not valid JSON: {ex.Message}"]
            });
        }
    }

    private static async Task<IResult> StopWorkspaceAsync(
        string workspaceId,
        ICommandDispatcher dispatcher,
        IVirtualActorProvider actors,
        IPluginInstallationAuthority authority,
        HttpContext context,
        ManagementAdmission admission,
        CancellationToken ct)
    {
        var denial = await authority.DenialAsync(context, workspaceId,
            PluginInstallationAuthority.StopWorkspaceGrant);
        if (denial is not null)
            return denial;
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
        var state = await workspace.GetStateAsync();
        var disableGrants = new List<string>();
        if (state.McpToolInstallations.Count > 0)
            disableGrants.Add(PluginInstallationAuthority.McpDisableGrant);
        if (state.DaprToolInstallations.Count > 0)
            disableGrants.Add(PluginInstallationAuthority.DaprDisableGrant);
        if (disableGrants.Count > 0)
        {
            disableGrants.Insert(0, PluginInstallationAuthority.StopWorkspaceGrant);
            denial = await authority.DenialAsync(context, workspaceId, disableGrants.ToArray());
            if (denial is not null)
                return denial;
        }
        else
            disableGrants.Add(PluginInstallationAuthority.StopWorkspaceGrant);

        var admitted = admission.Admit(context, workspaceId, "workspace:stop", workspaceId,
            disableGrants, Encoding.UTF8.GetBytes(workspaceId), out var managementId);
        if (admitted is not null)
            return admitted;
        try
        {
            var command = new StopWorkspaceCommand(WorkspaceId.From(workspaceId));
            await dispatcher.DispatchAsync<StopWorkspaceCommand, bool>(command, ct);
            return admission.Confirm(managementId, Results.NoContent());
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    // --- Validation ---

    private static Dictionary<string, string[]>? ValidateStartWorkspace(StartWorkspaceRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Manifest.Name))
            (errors ??= [])["manifest.name"] = ["Name is required."];
        if (string.IsNullOrWhiteSpace(request.Manifest.Version))
            (errors ??= [])["manifest.version"] = ["Version is required."];

        return errors;
    }
}
