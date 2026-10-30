using Weave.Agents.Channels;
using Weave.Agents.Chat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/agents")
            .WithTags("Agents");

        group.MapGet("/", GetAllAgentsAsync)
            .WithDescription("List all agents in a workspace.")
            .Produces<IEnumerable<AgentResponse>>();
        group.MapGet("/{agentName}", GetAgentAsync)
            .WithDescription("Get a single agent by name.")
            .Produces<AgentResponse>()
            .ProducesProblem(404);
        group.MapPost("/{agentName}/activate", ActivateAgentAsync)
            .WithDescription("Activate an agent with a definition.")
            .Produces<AgentResponse>(201)
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/{agentName}/deactivate", DeactivateAgentAsync)
            .WithDescription("Deactivate an agent.")
            .Produces(204)
            .ProducesProblem(409);
        group.MapPost("/{agentName}/messages", SendMessageAsync)
            .WithDescription("Send a message to an agent and get a response.")
            .Produces<ChatResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/{agentName}/messages/stream", SendMessageStreamEndpoint.HandleAsync)
            .WithDescription("Send a message to an agent and stream the response as Server-Sent Events.")
            .ProducesValidationProblem()
            .ProducesProblem(409);
        AgentTaskEndpoints.Map(group);

        return group;
    }

    // --- GET endpoints ---

    private static async Task<IResult> GetAllAgentsAsync(
        string workspaceId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetAllAgentStatesQuery(WorkspaceId.From(workspaceId));
        var states = await dispatcher.DispatchAsync<GetAllAgentStatesQuery, IReadOnlyList<AgentState>>(query, ct);
        return Results.Ok(states.Select(AgentResponse.FromState));
    }

    private static async Task<IResult> GetAgentAsync(
        string workspaceId,
        string agentName,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
        var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
        if (state.Definition is null)
            return ResultExtensions.NotFound($"Agent '{agentName}' not found in workspace '{workspaceId}'.");

        return Results.Ok(AgentResponse.FromState(state));
    }

    // --- POST endpoints ---

    private static async Task<IResult> ActivateAgentAsync(
        string workspaceId,
        string agentName,
        ActivateAgentRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateActivateAgent(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        try
        {
            var command = new ActivateAgentCommand(WorkspaceId.From(workspaceId), agentName, request.Definition);
            var state = await dispatcher.DispatchAsync<ActivateAgentCommand, AgentState>(command, ct);
            return Results.Created(
                $"/api/workspaces/{workspaceId}/agents/{agentName}",
                AgentResponse.FromState(state));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> DeactivateAgentAsync(
        string workspaceId,
        string agentName,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        try
        {
            var command = new DeactivateAgentCommand(WorkspaceId.From(workspaceId), agentName);
            await dispatcher.DispatchAsync<DeactivateAgentCommand, bool>(command, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> SendMessageAsync(
        string workspaceId,
        string agentName,
        SendMessageRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateSendMessage(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        try
        {
            var command = new SendAgentMessageCommand(
                WorkspaceId.From(workspaceId),
                agentName,
                new AgentMessage
                {
                    Role = request.Role,
                    Content = request.Content
                });
            var response = await dispatcher.DispatchAsync<SendAgentMessageCommand, AgentChatResponse>(command, ct);
            return Results.Ok(ChatResponse.FromResponse(response));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    // --- Validation ---

    private static Dictionary<string, string[]>? ValidateActivateAgent(ActivateAgentRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Definition.Model))
            (errors ??= [])["definition.model"] = ["Model is required."];

        return errors;
    }

    private static Dictionary<string, string[]>? ValidateSendMessage(SendMessageRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Content))
            (errors ??= [])["content"] = ["Content is required."];
        else if (request.Content.Length > 50_000)
            (errors ??= [])["content"] = ["Content must be 50000 characters or fewer."];

        return errors;
    }

}
