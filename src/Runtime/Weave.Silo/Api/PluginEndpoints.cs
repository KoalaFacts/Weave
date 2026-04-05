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
        group.MapGet("/catalog", GetCatalog);
        group.MapPost("/", ConnectPlugin);
        group.MapDelete("/{name}", DisconnectPlugin);

        return group;
    }

    private static IResult GetAllPlugins(IPluginRegistry registry)
    {
        return Results.Ok(registry.GetAll());
    }

    private static IResult GetCatalog(IPluginRegistry registry)
    {
        return Results.Ok(registry.GetCatalog());
    }

    private static async Task<IResult> ConnectPlugin(IPluginRegistry registry, ConnectPluginRequest request)
    {
        var basicErrors = ValidateConnectPlugin(request);
        if (basicErrors is not null)
            return ResultExtensions.ValidationFailed(basicErrors);

        var catalog = registry.GetCatalog();
        var (configErrors, warnings) = ValidatePluginConfig(request, catalog);
        if (configErrors is not null)
            return ResultExtensions.ValidationFailed(configErrors);

        var definition = new PluginDefinition
        {
            Type = request.Type,
            Description = request.Description,
            Config = request.Config is not null ? new(request.Config) : []
        };

        var status = await registry.ConnectAsync(request.Name, definition);
        if (!status.IsConnected)
            return Results.Problem(detail: status.Error, statusCode: 422, title: "Plugin Connection Failed");

        return Results.Ok(new ConnectPluginResponse { Status = status, Warnings = warnings });
    }

    private static async Task<IResult> DisconnectPlugin(IPluginRegistry registry, string name)
    {
        try
        {
            var status = await registry.DisconnectAsync(name);
            return Results.Ok(status);
        }
        catch (KeyNotFoundException ex)
        {
            return ResultExtensions.NotFound(ex.Message);
        }
    }

    private static Dictionary<string, string[]>? ValidateConnectPlugin(ConnectPluginRequest request)
    {
        Dictionary<string, string[]>? errors = null;
        if (string.IsNullOrWhiteSpace(request.Name))
            (errors ??= [])["name"] = ["Name is required."];
        if (string.IsNullOrWhiteSpace(request.Type))
            (errors ??= [])["type"] = ["Type is required."];
        return errors;
    }

    private static (Dictionary<string, string[]>? Errors, List<string> Warnings) ValidatePluginConfig(
        ConnectPluginRequest request,
        IReadOnlyList<PluginSchema> catalog)
    {
        var warnings = new List<string>();
        var schema = catalog.FirstOrDefault(s => string.Equals(s.Type, request.Type, StringComparison.OrdinalIgnoreCase));
        if (schema is null)
            return (null, warnings);

        Dictionary<string, string[]>? errors = null;
        var config = request.Config ?? new Dictionary<string, string>();
        var knownKeys = new HashSet<string>(schema.Config.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var field in schema.Config)
        {
            if (field.Required && !config.ContainsKey(field.Name))
                (errors ??= [])[$"config.{field.Name}"] = [$"Required config field '{field.Name}' is missing."];
        }

        foreach (var key in config.Keys)
        {
            if (!knownKeys.Contains(key))
                warnings.Add($"Unknown config key '{key}' for plugin type '{request.Type}'.");
        }

        return (errors, warnings);
    }
}

public sealed record ConnectPluginRequest
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string>? Config { get; init; }
}

public sealed record ConnectPluginResponse
{
    public required PluginStatus Status { get; init; }
    public List<string> Warnings { get; init; } = [];
}
