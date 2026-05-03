using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

internal static class SkillSuggestionEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/suggestions", GetSuggestedSkillsAsync)
            .WithDescription("List suggested skills awaiting review.")
            .Produces<IEnumerable<SkillSuggestionResponse>>();
        group.MapPost("/suggestions/{skillId}/accept", AcceptSuggestedSkillAsync)
            .WithDescription("Accept a suggested skill and add it to memory.")
            .Produces<SkillResponse>()
            .ProducesProblem(404);
        group.MapPost("/suggestions/{skillId}/reject", RejectSuggestedSkillAsync)
            .WithDescription("Reject a suggested skill.")
            .Produces(204)
            .ProducesProblem(404);
    }

    private static async Task<IResult> GetSuggestedSkillsAsync(
        string workspaceId,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actor = actors.GetActor<Agents.Actors.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var suggestions = await actor.GetSuggestedSkillsAsync(SkillTokenFactory.MintRead(tokenService, workspaceId));
        return Results.Ok(suggestions.Select(SkillSuggestionResponse.FromSuggestion));
    }

    private static async Task<IResult> AcceptSuggestedSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actor = actors.GetActor<Agents.Actors.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var skill = await actor.AcceptSuggestedSkillAsync(SkillId.From(skillId), SkillTokenFactory.MintWrite(tokenService, workspaceId));
        return skill is null
            ? ResultExtensions.NotFound($"Skill suggestion '{skillId}' not found.")
            : Results.Ok(SkillResponse.FromDocument(skill));
    }

    private static async Task<IResult> RejectSuggestedSkillAsync(
        string workspaceId,
        string skillId,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actor = actors.GetActor<Agents.Actors.ISkillMemoryActor>(VirtualActorId.From(workspaceId));
        var rejected = await actor.RejectSuggestedSkillAsync(SkillId.From(skillId), SkillTokenFactory.MintWrite(tokenService, workspaceId));
        return rejected
            ? Results.NoContent()
            : ResultExtensions.NotFound($"Skill suggestion '{skillId}' not found.");
    }
}
