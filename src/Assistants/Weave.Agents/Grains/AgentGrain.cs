using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class AgentGrain(
    IGrainFactory grainFactory,
    IAgentChatPipeline chatPipeline,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<AgentGrain> logger,
    [PersistentState("agent", "Default")] IPersistentState<AgentState> persistentState) : Grain, IAgentGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await persistentState.ReadStateAsync(cancellationToken);

        var key = TryGetPrimaryKeyString();
        if (string.IsNullOrWhiteSpace(persistentState.State.AgentId))
        {
            ApplyIdentity(key, persistentState.State.WorkspaceId);
            await persistentState.WriteStateAsync(cancellationToken);
        }

        if (persistentState.State.Definition is not null)
            chatPipeline.Initialize(persistentState.State.AgentId, persistentState.State.Model);
    }

    public async Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition)
    {
        if (persistentState.State.Status is AgentStatus.Active or AgentStatus.Busy)
            return persistentState.State;

        EnsureIdentity(workspaceId);

        persistentState.State.Status = AgentStatus.Activating;
        persistentState.State.Model = definition.Model;
        persistentState.State.MaxConcurrentTasks = definition.MaxConcurrentTasks;
        persistentState.State.Definition = definition;

        var context = new LifecycleContext
        {
            WorkspaceId = workspaceId,
            AgentName = persistentState.State.AgentName,
            Phase = LifecyclePhase.AgentActivating
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentActivating, context, CancellationToken.None);

            chatPipeline.Reset();
            chatPipeline.Initialize(persistentState.State.AgentId, definition.Model);

            persistentState.State.Status = AgentStatus.Active;
            persistentState.State.ActivatedAt = DateTimeOffset.UtcNow;
            persistentState.State.DeactivatedAt = null;
            persistentState.State.ErrorMessage = null;
            persistentState.State.LastActive = persistentState.State.ActivatedAt;

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.AgentActivated,
                context with { Phase = LifecyclePhase.AgentActivated },
                CancellationToken.None);

            await eventBus.PublishAsync(new AgentActivatedEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = workspaceId,
                Model = definition.Model,
                Tools = definition.Tools
            }, CancellationToken.None);

            logger.LogInformation(
                "Agent {AgentName} activated in workspace {WorkspaceId}",
                persistentState.State.AgentName,
                workspaceId);
        }
        catch (Exception ex)
        {
            persistentState.State.Status = AgentStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();

            await eventBus.PublishAsync(new AgentErrorEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = workspaceId,
                ErrorMessage = ex.Message
            }, CancellationToken.None);

            logger.LogError(ex, "Failed to activate agent {AgentName}", persistentState.State.AgentName);
            throw;
        }

        return persistentState.State;
    }

    public async Task DeactivateAsync()
    {
        if (persistentState.State.Status is AgentStatus.Idle or AgentStatus.Deactivating)
            return;

        persistentState.State.Status = AgentStatus.Deactivating;

        var context = new LifecycleContext
        {
            WorkspaceId = persistentState.State.WorkspaceId,
            AgentName = persistentState.State.AgentName,
            Phase = LifecyclePhase.AgentDeactivating
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentDeactivating, context, CancellationToken.None);

            chatPipeline.Reset();

            persistentState.State.Status = AgentStatus.Idle;
            persistentState.State.DeactivatedAt = DateTimeOffset.UtcNow;
            persistentState.State.ActiveTasks.Clear();
            persistentState.State.ConnectedTools.Clear();

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.AgentDeactivated,
                context with { Phase = LifecyclePhase.AgentDeactivated },
                CancellationToken.None);

            await eventBus.PublishAsync(new AgentDeactivatedEvent
            {
                SourceId = persistentState.State.AgentId,
                AgentName = persistentState.State.AgentName,
                WorkspaceId = persistentState.State.WorkspaceId
            }, CancellationToken.None);

            logger.LogInformation("Agent {AgentName} deactivated", persistentState.State.AgentName);
        }
        catch (Exception ex)
        {
            persistentState.State.Status = AgentStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();
            logger.LogError(ex, "Failed to deactivate agent {AgentName}", persistentState.State.AgentName);
            throw;
        }
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

        var task = persistentState.State.SubmitTask(description);
        await persistentState.WriteStateAsync();

        logger.LogInformation("Task {TaskId} submitted to agent {AgentName}", task.TaskId, persistentState.State.AgentName);
        return task;
    }

    public async Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof)
    {
        if (!success)
        {
            persistentState.State.FailTask(taskId, proof);
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

        persistentState.State.SetAwaitingReview(taskId, proof);
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

        var verifier = grainFactory.GetGrain<IProofVerifierGrain>(persistentState.State.WorkspaceId.ToString());
        _ = verifier.VerifyAsync(
            persistentState.State.WorkspaceId,
            persistentState.State.AgentName,
            taskId,
            proof);
    }

    public async Task ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback = null, VerificationRecord? verification = null)
    {
        if (accepted)
            persistentState.State.AcceptTask(taskId, feedback, verification);
        else
            persistentState.State.RejectTask(taskId, feedback, verification);

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

    private void EnsureIdentity(WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(persistentState.State.AgentId))
        {
            if (persistentState.State.WorkspaceId.IsEmpty)
                persistentState.State.WorkspaceId = workspaceId;

            if (string.IsNullOrWhiteSpace(persistentState.State.AgentName))
                persistentState.State.AgentName = GetAgentName(persistentState.State.AgentId);

            return;
        }

        ApplyIdentity(TryGetPrimaryKeyString(), workspaceId);
    }

    private void ApplyIdentity(string? key, WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var parts = key.Split('/', 2);
            persistentState.State.AgentId = key;
            persistentState.State.WorkspaceId = WorkspaceId.From(parts.Length > 1 ? parts[0] : key);
            persistentState.State.AgentName = parts.Length > 1 ? parts[1] : key;
            return;
        }

        persistentState.State.WorkspaceId = workspaceId;
        persistentState.State.AgentName = string.IsNullOrWhiteSpace(persistentState.State.AgentName)
            ? "agent"
            : persistentState.State.AgentName;
        persistentState.State.AgentId = $"{workspaceId}/{persistentState.State.AgentName}";
    }

    private static string GetAgentName(string agentId)
    {
        var separatorIndex = agentId.IndexOf('/', StringComparison.Ordinal);
        return separatorIndex >= 0 && separatorIndex < agentId.Length - 1
            ? agentId[(separatorIndex + 1)..]
            : agentId;
    }

    private string? TryGetPrimaryKeyString()
    {
        try
        {
            return this.GetPrimaryKeyString();
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }
}
