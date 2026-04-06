using Weave.Agents.Grains;

namespace Weave.Silo.Api;

public static class ToolEndpoints
{
    public static RouteGroupBuilder MapToolEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/tools")
            .WithTags("Tools");

        group.MapGet("/", GetAllTools)
            .WithDescription("List all tool connections in a workspace.")
            .Produces<IEnumerable<ToolConnectionResponse>>();
        group.MapGet("/{toolName}", GetTool)
            .WithDescription("Get a single tool connection by name.")
            .Produces<ToolConnectionResponse>()
            .ProducesProblem(404);

        return group;
    }

    // --- GET endpoints ---

    private static async Task<IResult> GetAllTools(
        string workspaceId,
        IGrainFactory grainFactory,
        CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IToolRegistryGrain>(workspaceId);
        var connections = await grain.GetAllConnectionsAsync();
        return Results.Ok(connections.Select(ToolConnectionResponse.FromConnection));
    }

    private static async Task<IResult> GetTool(
        string workspaceId,
        string toolName,
        IGrainFactory grainFactory,
        CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<IToolRegistryGrain>(workspaceId);
        var connection = await grain.GetConnectionAsync(toolName);
        if (connection is null)
            return ResultExtensions.NotFound($"Tool '{toolName}' not found in workspace '{workspaceId}'.");

        return Results.Ok(ToolConnectionResponse.FromConnection(connection));
    }
}
