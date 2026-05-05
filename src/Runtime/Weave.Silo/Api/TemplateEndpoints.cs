using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Templates;
using Weave.Workspaces.Registry;

namespace Weave.Silo.Api;

public static class TemplateEndpoints
{
    public static RouteGroupBuilder MapTemplateEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/templates")
            .WithTags("Templates");

        group.MapGet("/", ListPublishedAsync)
            .WithDescription("List published capability templates.")
            .Produces<IEnumerable<TemplateResponse>>();
        group.MapGet("/search", SearchAsync)
            .WithDescription("Search capability templates.")
            .Produces<IEnumerable<TemplateResponse>>();
        group.MapGet("/{templateId}", GetTemplateAsync)
            .WithDescription("Get a capability template by ID.")
            .Produces<TemplateResponse>()
            .ProducesProblem(404);
        group.MapPost("/", RegisterAsync)
            .WithDescription("Register a new capability template.")
            .Produces<TemplateResponse>(201)
            .ProducesValidationProblem();
        group.MapPost("/{templateId}/publish", PublishAsync)
            .WithDescription("Validate and publish a template.")
            .Produces<TemplateResponse>()
            .ProducesProblem(404)
            .ProducesProblem(422);
        group.MapPost("/{templateId}/deprecate", DeprecateAsync)
            .WithDescription("Deprecate a template.")
            .Produces(204)
            .ProducesProblem(404);

        return group;
    }

    private static async Task<IResult> ListPublishedAsync(
        int? offset,
        int? limit,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = GetTemplates(actors);
        var templates = await actor.ListPublishedAsync(offset ?? 0, limit ?? 50);
        return Results.Ok(templates.Select(TemplateResponse.FromTemplate));
    }

    private static async Task<IResult> SearchAsync(
        string? q,
        int? max,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = GetTemplates(actors);
        var templates = await actor.SearchAsync(q, max ?? 20);
        return Results.Ok(templates.Select(TemplateResponse.FromTemplate));
    }

    private static async Task<IResult> GetTemplateAsync(
        string templateId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = GetTemplates(actors);
        var template = await actor.GetAsync(TemplateId.From(templateId));
        if (template is null)
            return ResultExtensions.NotFound($"Template '{templateId}' not found.");

        return Results.Ok(TemplateResponse.FromTemplate(template));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterTemplateRequest request,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var errors = ValidateRegister(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var template = new CapabilityTemplate
        {
            TemplateId = TemplateId.New(),
            Name = request.Name,
            Description = request.Description,
            Version = request.Version,
            Author = request.Author,
            AgentDefinition = request.AgentDefinition,
            RequiredTools = request.RequiredTools ?? [],
            RequiredCapabilities = request.RequiredCapabilities ?? [],
            Tags = request.Tags ?? [],
            DefaultParameters = request.DefaultParameters ?? []
        };

        var actor = GetTemplates(actors);
        var stored = await actor.RegisterAsync(template);
        return Results.Created($"/api/templates/{stored.TemplateId}", TemplateResponse.FromTemplate(stored));
    }

    private static async Task<IResult> PublishAsync(
        string templateId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        try
        {
            var actor = GetTemplates(actors);
            var template = await actor.ValidateAndPublishAsync(TemplateId.From(templateId));
            if (template.Status != TemplateStatus.Published)
                return ResultExtensions.UnprocessableEntity("Template validation failed. Check validationResults.");

            return Results.Ok(TemplateResponse.FromTemplate(template));
        }
        catch (KeyNotFoundException ex)
        {
            return ResultExtensions.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> DeprecateAsync(
        string templateId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        try
        {
            var actor = GetTemplates(actors);
            await actor.DeprecateAsync(TemplateId.From(templateId));
            return Results.NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return ResultExtensions.NotFound(ex.Message);
        }
    }

    private static Dictionary<string, string[]>? ValidateRegister(RegisterTemplateRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Name))
            (errors ??= [])["name"] = ["Name is required."];
        if (string.IsNullOrWhiteSpace(request.Description))
            (errors ??= [])["description"] = ["Description is required."];
        if (string.IsNullOrWhiteSpace(request.Version))
            (errors ??= [])["version"] = ["Version is required."];
        if (string.IsNullOrWhiteSpace(request.Author))
            (errors ??= [])["author"] = ["Author is required."];

        return errors;
    }

    private static ICapabilityTemplateActor GetTemplates(IVirtualActorProvider actors) =>
        actors.GetActor<ICapabilityTemplateActor>(VirtualActorId.From("global"));
}
