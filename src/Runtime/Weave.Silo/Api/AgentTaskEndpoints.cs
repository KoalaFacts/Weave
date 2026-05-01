using Weave.Agents.Commands;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Shared.Cqrs;
using Weave.Shared.Ids;

namespace Weave.Silo.Api;

internal static class AgentTaskEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{agentName}/tasks", GetTasksAsync)
            .WithDescription("List tasks for an agent. Optionally filter by status (e.g. ?status=awaitingReview).")
            .Produces<IEnumerable<TaskResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(404);
        group.MapGet("/{agentName}/tasks/{taskId}", GetTaskAsync)
            .WithDescription("Get a single task by ID.")
            .Produces<TaskResponse>()
            .ProducesProblem(404);
        group.MapPost("/{agentName}/tasks", SubmitTaskAsync)
            .WithDescription("Submit a new task to an agent.")
            .Produces<TaskResponse>(201)
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/{agentName}/tasks/{taskId}/complete", CompleteTaskAsync)
            .WithDescription("Complete a task with proof of work.")
            .Produces<TaskResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409);
        group.MapPost("/{agentName}/tasks/{taskId}/review", ReviewTaskAsync)
            .WithDescription("Accept or reject a task awaiting review.")
            .Produces<TaskResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(409);
    }

    private static async Task<IResult> GetTasksAsync(
        string workspaceId,
        string agentName,
        string? status,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
        var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
        if (state.Definition is null)
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

    private static async Task<IResult> GetTaskAsync(
        string workspaceId,
        string agentName,
        string taskId,
        IQueryDispatcher dispatcher,
        CancellationToken ct)
    {
        var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
        var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
        if (state.Definition is null)
            return ResultExtensions.NotFound($"Agent '{agentName}' not found in workspace '{workspaceId}'.");

        var task = state.ActiveTasks.FirstOrDefault(t => t.TaskId == AgentTaskId.From(taskId));
        if (task is null)
            return ResultExtensions.NotFound($"Task '{taskId}' not found in agent '{agentName}'.");

        return Results.Ok(TaskResponse.FromInfo(task));
    }

    private static async Task<IResult> SubmitTaskAsync(
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

    private static async Task<IResult> CompleteTaskAsync(
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

    private static async Task<IResult> ReviewTaskAsync(
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

    private static Dictionary<string, string[]>? ValidateReviewTask(ReviewTaskRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (request.Feedback is { Length: > 5000 })
            (errors ??= [])["feedback"] = ["Feedback must be 5000 characters or fewer."];

        return errors;
    }
}
