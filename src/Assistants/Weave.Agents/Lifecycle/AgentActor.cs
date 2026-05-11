using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Chat;
using Weave.Agents.Memory;
using Weave.Agents.Pipeline;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Manifest;
namespace Weave.Agents.Lifecycle;

public sealed class AgentActor(
    IVirtualActorProvider actors,
    IAgentChatPipeline chatPipeline,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    IAgentVerificationDispatcher verificationDispatcher,
    ICapabilityTokenService tokenService,
    TimeProvider timeProvider,
    ILogger<AgentActor> logger,
    IActorState<AgentState> persistentState) : IAgentActor
{
    private readonly AgentSkillSuggester _skillSuggester = new(actors, tokenService, logger);
    private readonly AgentEpisodeRecorder _episodeRecorder = new(actors, timeProvider, logger);
    private readonly AgentLifecycle _lifecycle = new(
        chatPipeline,
        lifecycleManager,
        eventBus,
        timeProvider,
        new AgentEpisodeRecorder(actors, timeProvider, logger),
        logger,
        persistentState);
    private string? _key;

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _key = key;
        await persistentState.ReadStateAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(persistentState.State.AgentId))
        {
            ApplyIdentity(persistentState.State, key, persistentState.State.WorkspaceId);
            await persistentState.WriteStateAsync(cancellationToken);
        }

        if (persistentState.State.Definition is not null)
            await chatPipeline.InitializeAsync(persistentState.State.AgentId, persistentState.State.Definition, cancellationToken);
    }

    public async Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition)
    {
        if (persistentState.State.Status is AgentStatus.Active or AgentStatus.Busy)
            return persistentState.State;

        EnsureIdentity(persistentState.State, _key, workspaceId);
        try
        {
            return await _lifecycle.ActivateAsync(workspaceId, definition);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException)
        {
            await MarkErrorAndPublishAsync(ex, workspaceId);
            throw;
        }
    }

    public async Task DeactivateAsync()
    {
        if (persistentState.State.Status is AgentStatus.Idle or AgentStatus.Deactivating)
            return;

        try
        {
            await _lifecycle.DeactivateAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException)
        {
            await MarkErrorStateAsync(ex);
            logger.LogError(ex, "Failed to deactivate agent {AgentName}", persistentState.State.AgentName);
            throw;
        }
    }

    private async Task MarkErrorAndPublishAsync(Exception ex, WorkspaceId workspaceId)
    {
        await MarkErrorStateAsync(ex);

        await eventBus.PublishAsync(new AgentErrorEvent
        {
            SourceId = persistentState.State.AgentId,
            AgentName = persistentState.State.AgentName,
            WorkspaceId = workspaceId,
            ErrorMessage = ex.Message
        }, CancellationToken.None);

        logger.LogError(ex, "Failed to activate agent {AgentName}", persistentState.State.AgentName);
    }

    private async Task MarkErrorStateAsync(Exception ex)
    {
        persistentState.State.Status = AgentStatus.Error;
        persistentState.State.ErrorMessage = ex.Message;
        await persistentState.WriteStateAsync();
    }

    public Task<AgentState> GetStateAsync() => Task.FromResult(persistentState.State);

    public async Task<AgentChatResponse> SendAsync(AgentMessage message)
    {
        if (persistentState.State.Status is not (AgentStatus.Active or AgentStatus.Busy))
            throw new InvalidOperationException($"Agent {persistentState.State.AgentName} is not active (status: {persistentState.State.Status}).");

        var response = await chatPipeline.ExecuteAsync(persistentState.State, message);
        await persistentState.WriteStateAsync();
        return response;
    }

    public async Task<AgentTaskInfo> SubmitTaskAsync(string description)
    {
        if (persistentState.State.Status is not (AgentStatus.Active or AgentStatus.Busy))
            throw new InvalidOperationException($"Agent {persistentState.State.AgentName} is not active (status: {persistentState.State.Status}).");

        var task = persistentState.State.SubmitTask(description, timeProvider.GetUtcNow());
        await persistentState.WriteStateAsync();

        logger.LogInformation("Task {TaskId} submitted to agent {AgentName}", task.TaskId, persistentState.State.AgentName);
        return task;
    }

    public async Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof)
    {
        if (!success)
        {
            persistentState.State.FailTask(taskId, proof, timeProvider.GetUtcNow());
            await persistentState.WriteStateAsync();

            await eventBus.PublishAsync(new AgentTaskCompletedEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = persistentState.State.WorkspaceId,
                TaskId = taskId
            }, CancellationToken.None);

            logger.LogInformation("Task {TaskId} failed on agent {AgentName}", taskId, persistentState.State.AgentName);
            return;
        }

        persistentState.State.SetAwaitingReview(taskId, proof, timeProvider.GetUtcNow());
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new AgentTaskAwaitingReviewEvent
        {
            SourceId = persistentState.State.AgentId,
            AgentName = persistentState.State.AgentName,
            WorkspaceId = persistentState.State.WorkspaceId,
            TaskId = taskId
        }, CancellationToken.None);

        logger.LogInformation(
            "Task {TaskId} awaiting review on agent {AgentName} ({ProofCount} proof items)",
            taskId,
            persistentState.State.AgentName,
            proof.Items.Count);

        await verificationDispatcher.EnqueueAsync(
            new AgentVerificationRequest(
                persistentState.State.WorkspaceId,
                persistentState.State.AgentName,
                taskId,
                proof),
            CancellationToken.None);
    }

    public async Task ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback = null, VerificationRecord? verification = null)
    {
        var now = timeProvider.GetUtcNow();
        if (accepted)
            persistentState.State.AcceptTask(taskId, feedback, verification, now);
        else
            persistentState.State.RejectTask(taskId, feedback, verification, now);

        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new AgentTaskReviewedEvent
        {
            SourceId = persistentState.State.AgentId,
            AgentName = persistentState.State.AgentName,
            WorkspaceId = persistentState.State.WorkspaceId,
            TaskId = taskId,
            Accepted = accepted
        }, CancellationToken.None);

        logger.LogInformation(
            "Task {TaskId} reviewed on agent {AgentName} (accepted: {Accepted})",
            taskId,
            persistentState.State.AgentName,
            accepted);

        if (accepted)
        {
            await _skillSuggester.SuggestFromTaskAsync(persistentState.State, taskId);
            if (await _episodeRecorder.StoreTaskEpisodeAsync(persistentState.State, taskId))
                await persistentState.WriteStateAsync();
        }
    }

    public async Task ConnectToolAsync(string toolName)
    {
        if (!persistentState.State.ConnectedTools.Contains(toolName, StringComparer.Ordinal))
            persistentState.State.ConnectedTools.Add(toolName);

        await persistentState.WriteStateAsync();
    }

    public async Task DisconnectToolAsync(string toolName)
    {
        persistentState.State.ConnectedTools.Remove(toolName);
        await persistentState.WriteStateAsync();
    }

    private static void EnsureIdentity(AgentState state, string? key, WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(state.AgentId))
        {
            if (state.WorkspaceId.IsEmpty)
                state.WorkspaceId = workspaceId;

            if (string.IsNullOrWhiteSpace(state.AgentName))
                state.AgentName = GetAgentName(state.AgentId);

            return;
        }

        ApplyIdentity(state, key, workspaceId);
    }

    private static void ApplyIdentity(AgentState state, string? key, WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var parts = key.Split('/', 2);
            state.AgentId = key;
            state.WorkspaceId = WorkspaceId.From(parts.Length > 1 ? parts[0] : key);
            state.AgentName = parts.Length > 1 ? parts[1] : key;
            return;
        }

        state.WorkspaceId = workspaceId;
        state.AgentName = string.IsNullOrWhiteSpace(state.AgentName)
            ? "agent"
            : state.AgentName;
        state.AgentId = $"{workspaceId}/{state.AgentName}";
    }

    private static string GetAgentName(string agentId)
    {
        var separatorIndex = agentId.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex >= 0 && separatorIndex < agentId.Length - 1
            ? agentId[(separatorIndex + 1)..]
            : agentId;
    }

}
