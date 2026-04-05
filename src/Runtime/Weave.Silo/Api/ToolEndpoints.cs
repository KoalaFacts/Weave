using Weave.Agents.Grains;

namespace Weave.Silo.Api;

public static class ToolEndpoints
{
    public static RouteGroupBuilder MapToolEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/tools")
            .WithTags("Tools");

        group.MapGet("/", GetAllTools);
        group.MapGet("/{toolName}", GetTool);

        return group;
    }

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
        return connection is null
            ? ResultExtensions.NotFound($"Tool '{toolName}' not found in workspace '{workspaceId}'.")
            : Results.Ok(ToolConnectionResponse.FromConnection(connection));
    }
}
