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

        group.MapGet("/", GetAllAgents);
        group.MapGet("/{agentName}", GetAgent);
        group.MapPost("/{agentName}/activate", ActivateAgent);
        group.MapPost("/{agentName}/deactivate", DeactivateAgent);
        group.MapPost("/{agentName}/messages", SendMessage);
        group.MapPost("/{agentName}/tasks", SubmitTask);
        group.MapPost("/{agentName}/tasks/{taskId}/complete", CompleteTask);
        group.MapPost("/{agentName}/tasks/{taskId}/review", ReviewTask);

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
        return Results.Ok(AgentResponse.FromState(state));
    }

    private static async Task<IResult> ActivateAgent(
        string workspaceId,
        string agentName,
        ActivateAgentRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var command = new ActivateAgentCommand(WorkspaceId.From(workspaceId), agentName, request.Definition);
        var state = await dispatcher.DispatchAsync<ActivateAgentCommand, AgentState>(command, ct);
        return Results.Ok(AgentResponse.FromState(state));
    }

    private static async Task<IResult> DeactivateAgentAsync(
        string workspaceId,
        string agentName,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var command = new DeactivateAgentCommand(WorkspaceId.From(workspaceId), agentName);
        await dispatcher.DispatchAsync<DeactivateAgentCommand, bool>(command, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SubmitTaskAsync(
        string workspaceId,
        string agentName,
        SubmitTaskRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var command = new SubmitAgentTaskCommand(WorkspaceId.From(workspaceId), agentName, request.Description);
        var info = await dispatcher.DispatchAsync<SubmitAgentTaskCommand, AgentTaskInfo>(command, ct);
        return Results.Created(
            $"/api/workspaces/{workspaceId}/agents/{agentName}/tasks/{info.TaskId}",
            TaskResponse.FromInfo(info));
    }

    private static async Task<IResult> SendMessage(
        string workspaceId,
        string agentName,
        SendMessageRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
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

    private static async Task<IResult> CompleteTaskAsync(
        string workspaceId,
        string agentName,
        string taskId,
        CompleteTaskRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
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

    private static async Task<IResult> ReviewTask(
        string workspaceId,
        string agentName,
        string taskId,
        ReviewTaskRequest request,
        ICommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var command = new ReviewAgentTaskCommand(
            WorkspaceId.From(workspaceId), agentName, AgentTaskId.From(taskId), request.Accepted, request.Feedback);
        var info = await dispatcher.DispatchAsync<ReviewAgentTaskCommand, AgentTaskInfo>(command, ct);
        return Results.Ok(TaskResponse.FromInfo(info));
    }
}
