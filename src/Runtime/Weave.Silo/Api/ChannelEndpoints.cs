using Weave.Agents.Commands;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

public static class ChannelEndpoints
{
    public static RouteGroupBuilder MapChannelEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/channels")
            .WithTags("Channels");

        group.MapGet("/", GetChannelsAsync)
            .WithDescription("List all registered channels.")
            .Produces<IEnumerable<ChannelResponse>>();
        group.MapPost("/", RegisterChannelAsync)
            .WithDescription("Register a new messaging channel.")
            .Produces<ChannelResponse>(201)
            .ProducesValidationProblem();
        group.MapDelete("/{channelId}", UnregisterChannelAsync)
            .WithDescription("Unregister a channel.")
            .Produces(204);
        group.MapPost("/inbound", RouteInboundAsync)
            .WithDescription("Webhook entry point for inbound messages.")
            .Produces<OutboundMessageResponse>()
            .ProducesValidationProblem();

        return group;
    }

    private static async Task<IResult> GetChannelsAsync(
        string workspaceId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetChannelsQuery(WorkspaceId.From(workspaceId));
        var channels = await dispatcher.DispatchAsync<GetChannelsQuery, IReadOnlyList<ChannelConfig>>(query, ct);
        return Results.Ok(channels.Select(ChannelResponse.FromConfig));
    }

    private static async Task<IResult> RegisterChannelAsync(
        string workspaceId,
        RegisterChannelRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateRegisterChannel(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var config = new ChannelConfig
        {
            ChannelId = ChannelId.New(),
            Type = request.Type,
            Name = request.Name,
            Config = request.Config ?? [],
            TargetAgent = request.TargetAgent,
            Enabled = true
        };

        var command = new RegisterChannelCommand(WorkspaceId.From(workspaceId), config);
        await dispatcher.DispatchAsync<RegisterChannelCommand, bool>(command, ct);
        return Results.Created(
            $"/api/workspaces/{workspaceId}/channels/{config.ChannelId}",
            ChannelResponse.FromConfig(config));
    }

    private static async Task<IResult> UnregisterChannelAsync(
        string workspaceId,
        string channelId,
        IVirtualActorProvider actors,
        CancellationToken ct)
    {
        var actor = actors.GetActor<Agents.Actors.IChannelGatewayActor>(VirtualActorId.From(workspaceId));
        await actor.UnregisterChannelAsync(ChannelId.From(channelId));
        return Results.NoContent();
    }

    private static async Task<IResult> RouteInboundAsync(
        string workspaceId,
        InboundMessageRequest request,
        ICommandDispatcher dispatcher,
        ICapabilityTokenService tokenService,
        CancellationToken ct)
    {
        var errors = ValidateInboundMessage(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var message = new InboundMessage
        {
            ChannelId = ChannelId.From(request.ChannelId),
            SourceChannel = request.SourceChannel,
            SenderId = request.SenderId,
            SenderName = request.SenderName,
            Content = request.Content,
            ThreadId = request.ThreadId,
            Metadata = request.Metadata ?? []
        };

        var command = new RouteInboundMessageCommand(
            WorkspaceId.From(workspaceId),
            message,
            ChannelTokenFactory.MintInbound(tokenService, workspaceId, request.ChannelId));
        var outbound = await dispatcher.DispatchAsync<RouteInboundMessageCommand, OutboundMessage>(command, ct);
        return Results.Ok(OutboundMessageResponse.FromMessage(outbound));
    }

    private static Dictionary<string, string[]>? ValidateRegisterChannel(RegisterChannelRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Name))
            (errors ??= [])["name"] = ["Name is required."];

        return errors;
    }

    private static Dictionary<string, string[]>? ValidateInboundMessage(InboundMessageRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.ChannelId))
            (errors ??= [])["channelId"] = ["ChannelId is required."];
        if (string.IsNullOrWhiteSpace(request.SenderId))
            (errors ??= [])["senderId"] = ["SenderId is required."];
        if (string.IsNullOrWhiteSpace(request.Content))
            (errors ??= [])["content"] = ["Content is required."];

        return errors;
    }
}
