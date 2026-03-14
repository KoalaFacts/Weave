using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Api;

public static class PluginEndpoints
{
    public static RouteGroupBuilder MapPluginEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/plugins")
            .WithTags("Plugins");

        group.MapGet("/", GetAllPlugins);
        group.MapPost("/", ConnectPlugin);
        group.MapDelete("/{name}", DisconnectPlugin);

        return group;
    }

    private static IResult GetAllPlugins(IPluginRegistry registry)
    {
        return Results.Ok(registry.GetAll());
    }

    private static IResult ConnectPlugin(IPluginRegistry registry, ConnectPluginRequest request)
    {
        var definition = new PluginDefinition
        {
            Type = request.Type,
            Description = request.Description,
            Config = request.Config ?? []
        };

        var status = registry.Connect(request.Name, definition);
        return status.IsConnected ? Results.Ok(status) : Results.UnprocessableEntity(status);
    }

    private static IResult DisconnectPlugin(IPluginRegistry registry, string name)
    {
        var status = registry.Disconnect(name);
        return Results.Ok(status);
    }
}

public sealed record ConnectPluginRequest
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string>? Config { get; init; }
}
