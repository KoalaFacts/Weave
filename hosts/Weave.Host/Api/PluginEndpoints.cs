using Weave.Security.Tokens;
using Weave.Silo.Plugins;
using Weave.Tools.InstallDaprTool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Templates;
namespace Weave.Silo.Api;

public static class PluginEndpoints
{
    public static RouteGroupBuilder MapPluginEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/plugins")
            .WithTags("Plugins");

        group.MapGet("/", GetAllPluginsAsync)
            .WithDescription("List all connected plugins.")
            .Produces<IEnumerable<PluginStatus>>();
        group.MapGet("/catalog", GetCatalogAsync)
            .WithDescription("List available plugin types and their configuration schemas.")
            .Produces<IEnumerable<PluginSchema>>();
        group.MapGet("/composition", GetCompositionAsync)
            .WithDescription("Show active plugin registrations and activation status.")
            .Produces<IEnumerable<PluginCompositionEntry>>();
        group.MapPost("/", ConnectPluginAsync)
            .WithDescription("Connect a plugin with configuration. Unknown config keys are returned as warnings.")
            .Produces<ConnectPluginResponse>(201)
            .ProducesValidationProblem()
            .ProducesProblem(409)
            .ProducesProblem(422);
        group.MapDelete("/{**name}", DisconnectPluginAsync)
            .WithDescription("Disconnect a plugin by name.")
            .Produces(204)
            .ProducesProblem(404);

        return group;
    }

    // --- GET endpoints ---

    private static async Task<IResult> GetAllPluginsAsync(
        IPluginRegistry registry,
        CancellationToken ct)
    {
        return Results.Ok(registry.GetAll());
    }

    private static async Task<IResult> GetCatalogAsync(
        IPluginRegistry registry,
        CancellationToken ct)
    {
        return Results.Ok(registry.GetCatalog());
    }

    private static async Task<IResult> GetCompositionAsync(IPluginRegistry registry, CancellationToken ct) =>
        Results.Ok(await registry.GetCompositionAsync(ct));

    // --- POST/DELETE endpoints ---

    private static async Task<IResult> ConnectPluginAsync(
        ConnectPluginRequest request,
        IPluginRegistry registry,
        ICapabilityTokenService tokenService,
        IVirtualActorProvider actors,
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

            var installed = await FindDaprInstallationAsync(request.Name, actors);
            if (installed is not null || string.Equals(request.Type, "dapr_tools", StringComparison.OrdinalIgnoreCase))
            {
                if (installed is null)
                    return ResultExtensions.Conflict("Install this Dapr tools plugin through a workspace manifest first.");
                if (!string.Equals(request.Type, "dapr_tools", StringComparison.OrdinalIgnoreCase))
                    return ResultExtensions.Conflict("This installation is bound to the Dapr tools plugin type.");
                if (!int.TryParse(definition.Config.GetValueOrDefault("port"), out var port)
                    || port != installed.Value.Installation.Port
                    || !string.Equals(installed.Value.Installation.ConfigDigest,
                        DaprToolInstallation.ComputeConfigDigest(port), StringComparison.Ordinal))
                    return ResultExtensions.Conflict("The Dapr tools configuration differs from the installed revision.");
            }

            using var source = PluginTokenFactory.MintInvoke(tokenService, request.Name, ct);
            var status = await registry.ConnectAsync(request.Name, definition, source.Token);
            if (!status.IsConnected)
                return ResultExtensions.UnprocessableEntity(status.Error ?? "Plugin connection failed.");

            if (installed is not null)
            {
                try
                {
                    await installed.Value.Workspace.SetDaprToolInstallationEnabledAsync(
                        installed.Value.Installation.PluginName, true);
                }
                catch
                {
                    await registry.DisconnectAsync(request.Name, source.Token);
                    throw;
                }
            }

            return Results.Created(
                $"/api/plugins/{request.Name}",
                new ConnectPluginResponse { Status = status, Warnings = warnings });
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> DisconnectPluginAsync(
        string name,
        IPluginRegistry registry,
        ICapabilityTokenService tokenService,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var installed = await FindDaprInstallationAsync(name, actors);
        if (installed is not null)
            await installed.Value.Workspace.SetDaprToolInstallationEnabledAsync(
                installed.Value.Installation.PluginName, false);
        using var source = PluginTokenFactory.MintInvoke(tokenService, name, ct);
        var status = await registry.DisconnectAsync(name, source.Token);
        if (status.Error is not null)
        {
            if (installed is not null)
                return Results.NoContent();
            return ResultExtensions.NotFound($"Plugin '{name}' not found.");
        }

        return Results.NoContent();
    }

    private static async Task<(IWorkspaceActor Workspace, DaprToolInstallation Installation)?> FindDaprInstallationAsync(
        string name, IVirtualActorProvider actors)
    {
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == name.Length - 1)
            return null;
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(name[..slash]));
        var state = await workspace.GetStateAsync();
        var installation = state.DaprToolInstallations.SingleOrDefault(item =>
            string.Equals(item.Id, name, StringComparison.Ordinal));
        return installation is null ? null : (workspace, installation);
    }

    // --- Validation ---

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

        foreach (var field in schema.Config.Where(f => f.Required && !config.ContainsKey(f.Name)))
        {
            (errors ??= [])[$"config.{field.Name}"] = [$"Required config field '{field.Name}' is missing."];
        }

        foreach (var key in config.Keys.Where(key => !knownKeys.Contains(key)))
        {
            warnings.Add($"Unknown config key '{key}' for plugin type '{request.Type}'.");
        }

        return (errors, warnings);
    }
}
