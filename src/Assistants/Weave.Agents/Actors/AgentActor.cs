using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Actors;

public sealed class AgentActor(
    IVirtualActorProvider actors,
    IAgentChatPipeline chatPipeline,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<AgentActor> logger,
    IActorState<AgentState> persistentState) : IAgentActor
{
    private readonly AgentIdentity _identity = new();
    private readonly AgentSkillSuggester _skillSuggester = new(actors, new AgentSkillExtractor(), logger);
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
            _identity.Apply(persistentState.State, key, persistentState.State.WorkspaceId);
            await persistentState.WriteStateAsync(cancellationToken);
        }

        if (persistentState.State.Definition is not null)
            chatPipeline.Initialize(persistentState.State.AgentId, persistentState.State.Model);
    }

    public async Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition)
    {
        if (persistentState.State.Status is AgentStatus.Active or AgentStatus.Busy)
            return persistentState.State;

        _identity.Ensure(persistentState.State, _key, workspaceId);
        return await _lifecycle.ActivateAsync(workspaceId, definition);
    }

    public async Task DeactivateAsync()
    {
        if (persistentState.State.Status is AgentStatus.Idle or AgentStatus.Deactivating)
            return;

        await _lifecycle.DeactivateAsync();
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

        var verifier = actors.GetActor<IProofVerifierActor>(VirtualActorId.From(persistentState.State.WorkspaceId.ToString()));
        // Fire-and-forget is intentional: VerifyAsync calls back into this grain via
        // ReviewTaskAsync, so awaiting would deadlock (Orleans single-threaded reentrancy).
        _ = Task.Run(async () =>
        {
            try
            {
                await verifier.VerifyAsync(
                    persistentState.State.WorkspaceId,
                    persistentState.State.AgentName,
                    taskId,
                    proof);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Proof verification dispatch failed for task {TaskId} on agent {AgentName}", taskId, persistentState.State.AgentName);
            }
        });
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

}
