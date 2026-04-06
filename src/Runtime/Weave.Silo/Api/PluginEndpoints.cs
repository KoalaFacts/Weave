using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Api;

public static class PluginEndpoints
{
    public static RouteGroupBuilder MapPluginEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/plugins")
            .WithTags("Plugins");

        group.MapGet("/", GetAllPlugins)
            .WithDescription("List all connected plugins.")
            .Produces<IEnumerable<PluginStatus>>();
        group.MapGet("/catalog", GetCatalog)
            .WithDescription("List available plugin types and their configuration schemas.")
            .Produces<IEnumerable<PluginSchema>>();
        group.MapPost("/", ConnectPlugin)
            .WithDescription("Connect a plugin with configuration. Unknown config keys are returned as warnings.")
            .Produces<ConnectPluginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409)
            .ProducesProblem(422);
        group.MapDelete("/{name}", DisconnectPlugin)
            .WithDescription("Disconnect a plugin by name.")
            .Produces(204)
            .ProducesProblem(404);

        return group;
    }

    private static async Task<IResult> GetAllPlugins(
        IPluginRegistry registry,
        CancellationToken ct)
    {
        _ = ct; // registry methods are synchronous
        return Results.Ok(registry.GetAll());
    }

    private static async Task<IResult> GetCatalog(
        IPluginRegistry registry,
        CancellationToken ct)
    {
        _ = ct;
        return Results.Ok(registry.GetCatalog());
    }

    private static async Task<IResult> ConnectPlugin(
        ConnectPluginRequest request,
        IPluginRegistry registry,
        CancellationToken ct)
    {
        var errors = ValidateConnectPlugin(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var catalog = registry.GetCatalog();
        var (configErrors, warnings) = ValidatePluginConfig(request, catalog);
        if (configErrors is not null)
            return ResultExtensions.ValidationFailed(configErrors);

        try
        {
            var definition = new PluginDefinition
            {
                Type = request.Type,
                Description = request.Description,
                Config = request.Config is not null ? new(request.Config) : []
            };

            var status = await registry.ConnectAsync(request.Name, definition);
            if (!status.IsConnected)
                return ResultExtensions.UnprocessableEntity(status.Error ?? "Plugin connection failed.");

            return Results.Ok(new ConnectPluginResponse { Status = status, Warnings = warnings });
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> DisconnectPlugin(
        string name,
        IPluginRegistry registry,
        CancellationToken ct)
    {
        var status = await registry.DisconnectAsync(name);
        if (status.Error is not null)
            return ResultExtensions.NotFound($"Plugin '{name}' not found.");

        return Results.NoContent();
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
