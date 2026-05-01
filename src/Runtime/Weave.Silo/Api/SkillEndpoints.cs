using Weave.Agents.Commands;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

public static class SkillEndpoints
{
    public static RouteGroupBuilder MapSkillEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/skills")
            .WithTags("Skills");

        group.MapGet("/", GetAllSkillsAsync)
            .WithDescription("List all skills in a workspace.")
            .Produces<IEnumerable<SkillResponse>>();
        group.MapGet("/search", SearchSkillsAsync)
            .WithDescription("Search skills by keyword.")
            .Produces<IEnumerable<SkillSearchResultResponse>>();
        SkillSuggestionEndpoints.Map(group);
        group.MapGet("/{skillId}", GetSkillAsync)
            .WithDescription("Get a single skill by ID.")
            .Produces<SkillResponse>()
            .ProducesProblem(404);
        group.MapPost("/{skillId}/archive", ArchiveSkillAsync)
            .WithDescription("Archive a skill so it no longer appears in list or search results.")
            .Produces<SkillResponse>()
            .ProducesProblem(404);
        group.MapPost("/{skillId}/restore", RestoreSkillAsync)
            .WithDescription("Restore an archived skill so it appears in list and search results again.")
            .Produces<SkillResponse>()
            .ProducesProblem(404);
        group.MapPost("/", StoreSkillAsync)
            .WithDescription("Store a skill document.")
            .Produces<SkillResponse>(201)
            .ProducesValidationProblem();
        group.MapDelete("/{skillId}", RemoveSkillAsync)
            .WithDescription("Remove a skill.")
            .Produces(204)
            .ProducesProblem(404);

        return group;
    }

    private static async Task<IResult> GetAllSkillsAsync(
        string workspaceId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actor = actors.GetActor<Agents.Actors.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var skills = await actor.GetAllSkillsAsync();
        return Results.Ok(skills.Select(SkillResponse.FromDocument));
    }

    private static async Task<IResult> SearchSkillsAsync(
        string workspaceId,
        string? q,
        int? max,
        double? minSuccessRate,
        bool? preferRecent,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var options = new SkillSearchOptions
        {
            MinSuccessRate = minSuccessRate ?? 0,
            PreferRecent = preferRecent ?? false
        };
        var query = new SearchSkillsQuery(WorkspaceId.From(workspaceId), q ?? "", max ?? 5, options);
        var results = await dispatcher.DispatchAsync<SearchSkillsQuery, IReadOnlyList<SkillSearchResult>>(query, ct);
        return Results.Ok(results.Select(SkillSearchResultResponse.FromResult));
    }

    private static async Task<IResult> GetSkillAsync(
        string workspaceId,
        string skillId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        try
        {
            var query = new GetSkillQuery(WorkspaceId.From(workspaceId), SkillId.From(skillId));
            var skill = await dispatcher.DispatchAsync<GetSkillQuery, SkillDocument>(query, ct);
            return Results.Ok(SkillResponse.FromDocument(skill));
        }
        catch (KeyNotFoundException)
        {
            return ResultExtensions.NotFound($"Skill '{skillId}' not found.");
        }
    }

    private static async Task<IResult> StoreSkillAsync(
        string workspaceId,
        StoreSkillRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateStoreSkill(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var command = new StoreSkillCommand(WorkspaceId.From(workspaceId), SkillFromRequest(request));
        var stored = await dispatcher.DispatchAsync<StoreSkillCommand, SkillDocument>(command, ct);
        return Results.Created(
            $"/api/workspaces/{workspaceId}/skills/{stored.SkillId}",
            SkillResponse.FromDocument(stored));
    }

    private static async Task<IResult> ArchiveSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actor = actors.GetActor<Agents.Actors.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var archived = await actor.ArchiveSkillAsync(SkillId.From(skillId));
        return archived is null
            ? ResultExtensions.NotFound($"Skill '{skillId}' not found.")
            : Results.Ok(SkillResponse.FromDocument(archived));
    }

    private static async Task<IResult> RestoreSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actor = actors.GetActor<Agents.Actors.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var restored = await actor.RestoreSkillAsync(SkillId.From(skillId));
        return restored is null
            ? ResultExtensions.NotFound($"Skill '{skillId}' not found.")
            : Results.Ok(SkillResponse.FromDocument(restored));
    }

    private static async Task<IResult> RemoveSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = actors.GetActor<Agents.Actors.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        await actor.RemoveSkillAsync(SkillId.From(skillId));
        return Results.NoContent();
    }

    private static Dictionary<string, string[]>? ValidateStoreSkill(StoreSkillRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Title))
            (errors ??= [])["title"] = ["Title is required."];
        if (string.IsNullOrWhiteSpace(request.Description))
            (errors ??= [])["description"] = ["Description is required."];
        if (request.Steps is not { Count: > 0 })
            (errors ??= [])["steps"] = ["At least one step is required."];
        if (string.IsNullOrWhiteSpace(request.CreatedByAgent))
            (errors ??= [])["createdByAgent"] = ["CreatedByAgent is required."];

        return errors;
    }

    private static SkillDocument SkillFromRequest(StoreSkillRequest request)
    {
        return new SkillDocument
        {
            SkillId = SkillId.New(),
            Title = request.Title,
            Description = request.Description,
            Tags = request.Tags,
            Steps = request.Steps.Select((step, index) => new SkillStep
            {
                Order = index,
                Action = step.Action,
                ToolName = step.ToolName,
                ExpectedOutcome = step.ExpectedOutcome
            }).ToList(),
            ToolsUsed = request.ToolsUsed,
            CreatedByAgent = request.CreatedByAgent,
            OriginTaskDescription = request.OriginTaskDescription
        };
    }

}
