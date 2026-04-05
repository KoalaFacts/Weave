using Weave.Shared.Cqrs;
using Weave.Shared.Ids;
using Weave.Workspaces.Commands;
using Weave.Workspaces.Models;
using Weave.Workspaces.Queries;

namespace Weave.Silo.Api;

public static class WorkspaceEndpoints
{
    public static RouteGroupBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces")
            .WithTags("Workspaces");

        group.MapGet("/", GetAllWorkspaces);
        group.MapPost("/", StartWorkspace);
        group.MapDelete("/{workspaceId}", StopWorkspace);
        group.MapGet("/{workspaceId}", GetWorkspaceState);

        return group;
    }

    private static async Task<IResult> GetAllWorkspaces(
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var states = await dispatcher.DispatchAsync<GetAllWorkspaceStatesQuery, IReadOnlyList<WorkspaceState>>(
            new GetAllWorkspaceStatesQuery(),
            ct);
        return Results.Ok(states.Select(WorkspaceResponse.FromState));
    }

    private static async Task<IResult> StartWorkspace(
        StartWorkspaceRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var validationErrors = ValidateStartWorkspace(request);
        if (validationErrors is not null)
            return ResultExtensions.ValidationFailed(validationErrors);

        try
        {
            var workspaceId = WorkspaceId.New();
            var command = new StartWorkspaceCommand(workspaceId, request.Manifest);
            var state = await dispatcher.DispatchAsync<StartWorkspaceCommand, Workspaces.Models.WorkspaceState>(command, ct);
            return Results.Created($"/api/workspaces/{workspaceId}", WorkspaceResponse.FromState(state));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> StopWorkspace(
        string workspaceId,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        try
        {
            var command = new StopWorkspaceCommand(WorkspaceId.From(workspaceId));
            await dispatcher.DispatchAsync<StopWorkspaceCommand, bool>(command, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> GetWorkspaceState(
        string workspaceId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetWorkspaceStateQuery(WorkspaceId.From(workspaceId));
        var state = await dispatcher.DispatchAsync<GetWorkspaceStateQuery, Workspaces.Models.WorkspaceState>(query, ct);
        if (state.WorkspaceId.IsEmpty)
            return ResultExtensions.NotFound($"Workspace '{workspaceId}' not found.");
        return Results.Ok(WorkspaceResponse.FromState(state));
    }

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
