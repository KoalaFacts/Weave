using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;

namespace Weave.Silo.Api;

public static class ToolEndpoints
{
    public static RouteGroupBuilder MapToolEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/tools")
            .WithTags("Tools");

        group.MapGet("/", GetAllToolsAsync)
            .WithDescription("List all tool connections in a workspace.")
            .Produces<IEnumerable<ToolConnectionResponse>>();
        group.MapGet("/{toolName}", GetToolAsync)
            .WithDescription("Get a single tool connection by name.")
            .Produces<ToolConnectionResponse>()
            .ProducesProblem(404);

        return group;
    }

    // --- GET endpoints ---

    private static async Task<IResult> GetAllToolsAsync(
        string workspaceId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
        var connections = await actor.GetAllConnectionsAsync();
        return Results.Ok(connections.Select(ToolConnectionResponse.FromConnection));
    }

    private static async Task<IResult> GetToolAsync(
        string workspaceId,
        string toolName,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
        var connection = await actor.GetConnectionAsync(toolName);
        if (connection is null)
            return ResultExtensions.NotFound($"Tool '{toolName}' not found in workspace '{workspaceId}'.");

        return Results.Ok(ToolConnectionResponse.FromConnection(connection));
    }
}
