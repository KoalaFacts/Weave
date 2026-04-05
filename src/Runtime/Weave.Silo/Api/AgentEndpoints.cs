using Weave.Agents.Commands;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId}/agents")
            .WithTags("Agents");

        group.MapGet("/", GetAllAgentsAsync);
        group.MapGet("/{agentName}", GetAgent);
        group.MapPost("/{agentName}/activate", ActivateAgent);
        group.MapPost("/{agentName}/deactivate", DeactivateAgent);
        group.MapPost("/{agentName}/messages", SendMessage);
        group.MapPost("/{agentName}/tasks", SubmitTask);
        group.MapPost("/{agentName}/tasks/{taskId}/complete", CompleteTask);
        group.MapPost("/{agentName}/tasks/{taskId}/review", ReviewTask);

        return group;
    }

    private static async Task<IResult> GetAllAgentsAsync(
        string workspaceId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetAllAgentStatesQuery(WorkspaceId.From(workspaceId));
        var states = await dispatcher.DispatchAsync<GetAllAgentStatesQuery, IReadOnlyList<AgentState>>(query, ct);
        return Results.Ok(states.Select(AgentResponse.FromState));
    }

    private static async Task<IResult> GetAgent(
        string workspaceId,
        string agentName,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        try
        {
            var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
            var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
            return Results.Ok(AgentResponse.FromState(state));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> ActivateAgent(
        string workspaceId,
        string agentName,
        ActivateAgentRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateActivateAgent(request);
        if (errors is not null)
            return Results.BadRequest(errors);

        try
        {
            var command = new ActivateAgentCommand(WorkspaceId.From(workspaceId), agentName, request.Definition);
            var state = await dispatcher.DispatchAsync<ActivateAgentCommand, AgentState>(command, ct);
            return Results.Ok(AgentResponse.FromState(state));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> DeactivateAgent(
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
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> SubmitTask(
        string workspaceId,
        string agentName,
        SubmitTaskRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateSubmitTask(request);
        if (errors is not null)
            return Results.BadRequest(errors);

        try
        {
            var command = new SubmitAgentTaskCommand(WorkspaceId.From(workspaceId), agentName, request.Description);
            var info = await dispatcher.DispatchAsync<SubmitAgentTaskCommand, AgentTaskInfo>(command, ct);
            return Results.Created(
                $"/api/workspaces/{workspaceId}/agents/{agentName}/tasks/{info.TaskId}",
                TaskResponse.FromInfo(info));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> SendMessage(
        string workspaceId,
        string agentName,
        SendMessageRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateSendMessage(request);
        if (errors is not null)
            return Results.BadRequest(errors);

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
            var response = await dispatcher.DispatchAsync<SendAgentMessageCommand, Weave.Agents.Models.AgentChatResponse>(command, ct);
            return Results.Ok(Api.AgentChatResponse.FromResponse(response));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> CompleteTask(
        string workspaceId,
        string agentName,
        string taskId,
        CompleteTaskRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var errors = ValidateCompleteTask(request);
        if (errors is not null)
            return Results.BadRequest(errors);

        try
        {
            var proof = new ProofOfWork
            {
                Items = request.Proof.Select(p => new ProofItem
                {
                    Type = Enum.Parse<ProofType>(p.Type, ignoreCase: true),
                    Label = p.Label,
                    Value = p.Value,
                    Uri = p.Uri
                }).ToList()
            };

            var command = new CompleteAgentTaskCommand(
                WorkspaceId.From(workspaceId), agentName, AgentTaskId.From(taskId), request.Success, proof);
            var info = await dispatcher.DispatchAsync<CompleteAgentTaskCommand, AgentTaskInfo>(command, ct);
            return Results.Ok(TaskResponse.FromInfo(info));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> ReviewTask(
        string workspaceId,
        string agentName,
        string taskId,
        ReviewTaskRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        try
        {
            var command = new ReviewAgentTaskCommand(
                WorkspaceId.From(workspaceId), agentName, AgentTaskId.From(taskId), request.Accepted, request.Feedback);
            var info = await dispatcher.DispatchAsync<ReviewAgentTaskCommand, AgentTaskInfo>(command, ct);
            return Results.Ok(TaskResponse.FromInfo(info));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static Dictionary<string, string[]>? ValidateSubmitTask(SubmitTaskRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Description))
            (errors ??= [])["description"] = ["Description is required."];
        else if (request.Description.Length > 1000)
            (errors ??= [])["description"] = ["Description must be 1000 characters or fewer."];

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

    private static Dictionary<string, string[]>? ValidateCompleteTask(CompleteTaskRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (request.Proof is not { Count: > 0 })
            (errors ??= [])["proof"] = ["At least one proof item is required."];
        else
        {
            for (var i = 0; i < request.Proof.Count; i++)
            {
                var item = request.Proof[i];
                if (string.IsNullOrWhiteSpace(item.Label))
                    (errors ??= [])[$"proof[{i}].label"] = ["Label is required."];
                if (string.IsNullOrWhiteSpace(item.Value))
                    (errors ??= [])[$"proof[{i}].value"] = ["Value is required."];
            }
        }

        return errors;
    }

    private static Dictionary<string, string[]>? ValidateActivateAgent(ActivateAgentRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Definition.Model))
            (errors ??= [])["definition.model"] = ["Model is required."];

        return errors;
    }
}
