using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
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
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        using var source = SkillTokenFactory.MintRead(tokenService, workspaceId, ct);
        var actor = actors.GetActor<Agents.Skills.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var skills = await actor.GetAllSkillsAsync(source.Token);
        return Results.Ok(skills.Select(SkillResponse.FromDocument));
    }

    private static async Task<IResult> SearchSkillsAsync(
        string workspaceId,
        string? q,
        int? max,
        double? minSuccessRate,
        bool? preferRecent,
        IQueryDispatcher dispatcher,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        var options = new SkillSearchOptions
        {
            MinSuccessRate = minSuccessRate ?? 0,
            PreferRecent = preferRecent ?? false
        };
        using var source = SkillTokenFactory.MintRead(tokenService, workspaceId, ct);
        var query = new SearchSkillsQuery(
            WorkspaceId.From(workspaceId),
            q ?? "",
            source.Token,
            max ?? 5,
            options);
        var results = await dispatcher.DispatchAsync<SearchSkillsQuery, IReadOnlyList<SkillSearchResult>>(query, ct);
        return Results.Ok(results.Select(SkillSearchResultResponse.FromResult));
    }

    private static async Task<IResult> GetSkillAsync(
        string workspaceId,
        string skillId,
        IQueryDispatcher dispatcher,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        try
        {
            using var source = SkillTokenFactory.MintRead(tokenService, workspaceId, ct);
            var query = new GetSkillQuery(
                WorkspaceId.From(workspaceId),
                SkillId.From(skillId),
                source.Token);
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
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        var errors = ValidateStoreSkill(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        using var source = SkillTokenFactory.MintWrite(tokenService, workspaceId, ct);
        var command = new StoreSkillCommand(
            WorkspaceId.From(workspaceId),
            SkillFromRequest(request),
            source.Token);
        var stored = await dispatcher.DispatchAsync<StoreSkillCommand, SkillDocument>(command, ct);
        return Results.Created(
            $"/api/workspaces/{workspaceId}/skills/{stored.SkillId}",
            SkillResponse.FromDocument(stored));
    }

    private static async Task<IResult> ArchiveSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        using var source = SkillTokenFactory.MintWrite(tokenService, workspaceId, ct);
        var actor = actors.GetActor<Agents.Skills.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var archived = await actor.ArchiveSkillAsync(SkillId.From(skillId), source.Token);
        return archived is null
            ? ResultExtensions.NotFound($"Skill '{skillId}' not found.")
            : Results.Ok(SkillResponse.FromDocument(archived));
    }

    private static async Task<IResult> RestoreSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        using var source = SkillTokenFactory.MintWrite(tokenService, workspaceId, ct);
        var actor = actors.GetActor<Agents.Skills.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var restored = await actor.RestoreSkillAsync(SkillId.From(skillId), source.Token);
        return restored is null
            ? ResultExtensions.NotFound($"Skill '{skillId}' not found.")
            : Results.Ok(SkillResponse.FromDocument(restored));
    }

    private static async Task<IResult> RemoveSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        using var source = SkillTokenFactory.MintWrite(tokenService, workspaceId, ct);
        var actor = actors.GetActor<Agents.Skills.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        await actor.RemoveSkillAsync(SkillId.From(skillId), source.Token);
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
