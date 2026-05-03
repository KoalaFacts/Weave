using Weave.Agents.Commands;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/users")
            .WithTags("Users");

        group.MapGet("/{userId}/profile", GetProfileAsync)
            .WithDescription("Get a user's profile and context.")
            .Produces<UserProfileResponse>();
        group.MapPut("/{userId}/preferences", SetPreferenceAsync)
            .WithDescription("Set a user preference.")
            .Produces(204)
            .ProducesValidationProblem();
        group.MapPut("/{userId}/context", SetDomainContextAsync)
            .WithDescription("Set a user's domain context entry.")
            .Produces(204)
            .ProducesValidationProblem();
        group.MapDelete("/{userId}", ClearProfileAsync)
            .WithDescription("Clear a user's profile.")
            .Produces(204);

        return group;
    }

    private static async Task<IResult> GetProfileAsync(
        string workspaceId,
        string userId,
        IQueryDispatcher dispatcher,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        using var source = UserTokenFactory.MintRead(tokenService, workspaceId, userId, ct);
        var query = new GetUserProfileQuery(WorkspaceId.From(workspaceId), userId, source.Token);
        var profile = await dispatcher.DispatchAsync<GetUserProfileQuery, UserProfileState>(query, ct);
        return Results.Ok(UserProfileResponse.FromState(profile));
    }

    private static async Task<IResult> SetPreferenceAsync(
        string workspaceId,
        string userId,
        SetPreferenceRequest request,
        ICommandDispatcher dispatcher,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        var errors = ValidateKeyValue(request.Key, request.Value);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        using var source = UserTokenFactory.MintWrite(tokenService, workspaceId, userId, ct);
        var command = new SetUserPreferenceCommand(
            WorkspaceId.From(workspaceId),
            userId,
            request.Key,
            request.Value,
            source.Token);
        await dispatcher.DispatchAsync<SetUserPreferenceCommand, bool>(command, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetDomainContextAsync(
        string workspaceId,
        string userId,
        SetDomainContextRequest request,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        var errors = ValidateKeyValue(request.Key, request.Value);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        using var source = UserTokenFactory.MintWrite(tokenService, workspaceId, userId, ct);
        var actor = actors.GetActor<Agents.Actors.IUserModelActor>(VirtualActorId.Combine(workspaceId, userId));
        await actor.SetDomainContextAsync(request.Key, request.Value, source.Token);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearProfileAsync(
        string workspaceId,
        string userId,
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        using var source = UserTokenFactory.MintWrite(tokenService, workspaceId, userId, ct);
        var actor = actors.GetActor<Agents.Actors.IUserModelActor>(VirtualActorId.Combine(workspaceId, userId));
        await actor.ClearAsync(source.Token);
        return Results.NoContent();
    }

    private static Dictionary<string, string[]>? ValidateKeyValue(string key, string value)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(key))
            (errors ??= [])["key"] = ["Key is required."];
        if (string.IsNullOrWhiteSpace(value))
            (errors ??= [])["value"] = ["Value is required."];

        return errors;
    }
}
