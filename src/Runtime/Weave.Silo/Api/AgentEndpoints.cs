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

        group.MapGet("/", GetAllAgents)
            .WithDescription("List all agents in a workspace.")
            .Produces<IEnumerable<AgentResponse>>();
        group.MapGet("/{agentName}", GetAgent)
            .WithDescription("Get a single agent by name.")
            .Produces<AgentResponse>()
            .ProducesProblem(404);
        group.MapPost("/{agentName}/activate", ActivateAgent)
            .WithDescription("Activate an agent with a definition.")
            .Produces<AgentResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/{agentName}/deactivate", DeactivateAgent)
            .WithDescription("Deactivate an agent.")
            .Produces(204)
            .ProducesProblem(409);
        group.MapPost("/{agentName}/messages", SendMessage)
            .WithDescription("Send a message to an agent and get a response.")
            .Produces<AgentChatResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapGet("/{agentName}/tasks", GetTasks)
            .WithDescription("List tasks for an agent. Optionally filter by status (e.g. ?status=awaitingReview).")
            .Produces<IEnumerable<TaskResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(404);
        group.MapGet("/{agentName}/tasks/{taskId}", GetTask)
            .WithDescription("Get a single task by ID.")
            .Produces<TaskResponse>()
            .ProducesProblem(404);
        group.MapPost("/{agentName}/tasks", SubmitTask)
            .WithDescription("Submit a new task to an agent.")
            .Produces<TaskResponse>(201)
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/{agentName}/tasks/{taskId}/complete", CompleteTask)
            .WithDescription("Complete a task with proof of work.")
            .Produces<TaskResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/{agentName}/tasks/{taskId}/review", ReviewTask)
            .WithDescription("Accept or reject a task awaiting review.")
            .Produces<TaskResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409);

        return group;
    }

    private static async Task<IResult> GetAllAgents(
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
        var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
        var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
        if (string.IsNullOrWhiteSpace(state.AgentId))
            return ResultExtensions.NotFound($"Agent '{agentName}' not found in workspace '{workspaceId}'.");

        return Results.Ok(AgentResponse.FromState(state));
    }

    private static async Task<IResult> GetTasks(
        string workspaceId,
        string agentName,
        string? status,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
        var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
        if (string.IsNullOrWhiteSpace(state.AgentId))
            return ResultExtensions.NotFound($"Agent '{agentName}' not found in workspace '{workspaceId}'.");

        IEnumerable<AgentTaskInfo> tasks = state.ActiveTasks;

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<AgentTaskStatus>(status, ignoreCase: true, out var parsed))
                return ResultExtensions.ValidationFailed(new Dictionary<string, string[]>
                {
                    ["status"] = [$"'{status}' is not a valid task status."]
                });

            tasks = tasks.Where(t => t.Status == parsed);
        }

        return Results.Ok(tasks.Select(TaskResponse.FromInfo));
    }

    private static async Task<IResult> GetTask(
        string workspaceId,
        string agentName,
        string taskId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
        var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
        if (string.IsNullOrWhiteSpace(state.AgentId))
            return ResultExtensions.NotFound($"Agent '{agentName}' not found in workspace '{workspaceId}'.");

        var task = state.ActiveTasks.FirstOrDefault(t => t.TaskId == AgentTaskId.From(taskId));
        if (task is null)
            return ResultExtensions.NotFound($"Task '{taskId}' not found on agent '{agentName}'.");

        return Results.Ok(TaskResponse.FromInfo(task));
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
            return ResultExtensions.ValidationFailed(errors);

        try
        {
            var command = new ActivateAgentCommand(WorkspaceId.From(workspaceId), agentName, request.Definition);
            var state = await dispatcher.DispatchAsync<ActivateAgentCommand, AgentState>(command, ct);
            return Results.Ok(AgentResponse.FromState(state));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
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
            return ResultExtensions.Conflict(ex.Message);
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
            var response = await dispatcher.DispatchAsync<SendAgentMessageCommand, Weave.Agents.Models.AgentChatResponse>(command, ct);
            return Results.Ok(Api.AgentChatResponse.FromResponse(response));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
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
            return ResultExtensions.ValidationFailed(errors);

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
            return ResultExtensions.Conflict(ex.Message);
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
            return ResultExtensions.ValidationFailed(errors);

        try
        {
            var proof = new ProofOfWork
            {
                Items = request.Proof.Select(p => new ProofItem
                {
                    Type = p.Type,
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
            return ResultExtensions.Conflict(ex.Message);
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
        var errors = ValidateReviewTask(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        try
        {
            var command = new ReviewAgentTaskCommand(
                WorkspaceId.From(workspaceId), agentName, AgentTaskId.From(taskId), request.Accepted, request.Feedback);
            var info = await dispatcher.DispatchAsync<ReviewAgentTaskCommand, AgentTaskInfo>(command, ct);
            return Results.Ok(TaskResponse.FromInfo(info));
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
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

    private static Dictionary<string, string[]>? ValidateReviewTask(ReviewTaskRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (request.Feedback is { Length: > 5000 })
            (errors ??= [])["feedback"] = ["Feedback must be 5000 characters or fewer."];

        return errors;
    }
}
