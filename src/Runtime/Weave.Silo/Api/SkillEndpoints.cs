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
        group.MapGet("/{skillId}", GetSkillAsync)
            .WithDescription("Get a single skill by ID.")
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
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new SearchSkillsQuery(WorkspaceId.From(workspaceId), "", 1000);
        var results = await dispatcher.DispatchAsync<SearchSkillsQuery, IReadOnlyList<SkillSearchResult>>(query, ct);
        return Results.Ok(results.Select(r => SkillResponse.FromDocument(r.Skill)));
    }

    private static async Task<IResult> SearchSkillsAsync(
        string workspaceId,
        string? q,
        int? max,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new SearchSkillsQuery(WorkspaceId.From(workspaceId), q ?? "", max ?? 5);
        var results = await dispatcher.DispatchAsync<SearchSkillsQuery, IReadOnlyList<SkillSearchResult>>(query, ct);
        return Results.Ok(results.Select(SkillSearchResultResponse.FromResult));
    }

    private static async Task<IResult> GetSkillAsync(
        string workspaceId,
        string skillId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetSkillQuery(WorkspaceId.From(workspaceId), SkillId.From(skillId));
        var skill = await dispatcher.DispatchAsync<GetSkillQuery, SkillDocument?>(query, ct);
        if (skill is null)
            return ResultExtensions.NotFound($"Skill '{skillId}' not found.");

        return Results.Ok(SkillResponse.FromDocument(skill));
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

        var skill = new SkillDocument
        {
            SkillId = SkillId.New(),
            Title = request.Title,
            Description = request.Description,
            Tags = request.Tags,
            Steps = request.Steps.Select((s, i) => new SkillStep
            {
                Order = i,
                Action = s.Action,
                ToolName = s.ToolName,
                ExpectedOutcome = s.ExpectedOutcome
            }).ToList(),
            ToolsUsed = request.ToolsUsed,
            CreatedByAgent = request.CreatedByAgent,
            OriginTaskDescription = request.OriginTaskDescription
        };

        var command = new StoreSkillCommand(WorkspaceId.From(workspaceId), skill);
        var stored = await dispatcher.DispatchAsync<StoreSkillCommand, SkillDocument>(command, ct);
        return Results.Created(
            $"/api/workspaces/{workspaceId}/skills/{stored.SkillId}",
            SkillResponse.FromDocument(stored));
    }

    private static async Task<IResult> RemoveSkillAsync(
        string workspaceId,
        string skillId,
        IGrainFactory grainFactory,
        CancellationToken ct)
    {
        var grain = grainFactory.GetGrain<Agents.Grains.ISkillMemoryGrain>(workspaceId);
        await grain.RemoveSkillAsync(SkillId.From(skillId));
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
}
