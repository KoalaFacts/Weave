using System.Text;
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
    private string? _key;

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _key = key;
        await persistentState.ReadStateAsync(cancellationToken);

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
            persistentState.State.ActivatedAt = timeProvider.GetUtcNow();
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

            await TryExtractSessionEpisodeAsync();

            persistentState.State.Status = AgentStatus.Idle;
            persistentState.State.DeactivatedAt = timeProvider.GetUtcNow();
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
            await TryExtractSkillAsync(taskId);
            await TryExtractEpisodeAsync(taskId);
        }
    }

    private async Task TryExtractSkillAsync(AgentTaskId taskId)
    {
        var task = persistentState.State.ActiveTasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task?.Proof is null || task.Proof.Items.Count < 2)
            return;

        var skill = ExtractSkillFromTask(task, persistentState.State);
        if (skill is null)
            return;

        try
        {
            var skillActor = actors.GetActor<ISkillMemoryActor>(VirtualActorId.From(persistentState.State.WorkspaceId.ToString()));
            await skillActor.SuggestSkillAsync(skill, taskId.ToString());
            logger.LogInformation(
                "Suggested skill '{Title}' from task {TaskId}",
                skill.Title,
                taskId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to suggest skill from task {TaskId}", taskId);
        }
    }

    private async Task TryExtractEpisodeAsync(AgentTaskId taskId)
    {
        var task = persistentState.State.ActiveTasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null)
            return;

        var episode = ExtractEpisodeFromTask(task, persistentState.State, timeProvider.GetUtcNow());
        if (episode is null)
            return;

        try
        {
            var episodicActor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(persistentState.State.WorkspaceId.ToString()));
            await episodicActor.StoreEpisodeAsync(episode);
            persistentState.State.LastEpisodeHistoryIndex = persistentState.State.History.Count;
            await persistentState.WriteStateAsync();
            logger.LogInformation(
                "Stored episode '{Title}' from task {TaskId}",
                episode.Title,
                taskId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store episode from task {TaskId}", taskId);
        }
    }

    private async Task TryExtractSessionEpisodeAsync()
    {
        var state = persistentState.State;
        if (state.History.Count <= state.LastEpisodeHistoryIndex)
            return;

        var episode = ExtractEpisodeFromSession(state, timeProvider.GetUtcNow());
        if (episode is null)
            return;

        try
        {
            var episodicActor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
            await episodicActor.StoreEpisodeAsync(episode);
            state.LastEpisodeHistoryIndex = state.History.Count;
            logger.LogInformation(
                "Stored session episode '{Title}' for agent {AgentName}",
                episode.Title,
                state.AgentName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store session episode for agent {AgentName}", state.AgentName);
        }
    }

    internal static Episode? ExtractEpisodeFromTask(AgentTaskInfo task, AgentState state, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(task.Description))
            return null;

        var historySlice = state.History.Skip(state.LastEpisodeHistoryIndex).ToList();
        var narrative = BuildNarrative(historySlice, fallback: task.Description);
        var decisions = BuildDecisionsFromProof(task.Proof);
        var tags = BuildTags(task, state);

        return new Episode
        {
            EpisodeId = Shared.Ids.EpisodeId.New(),
            Title = task.Description.Length > 100 ? task.Description[..100] : task.Description,
            Narrative = narrative,
            AgentName = state.AgentName,
            Tags = tags,
            Decisions = decisions,
            SourceTaskId = task.TaskId.ToString(),
            SourceMessageIds = [],
            OccurredAt = occurredAt
        };
    }

    internal static Episode? ExtractEpisodeFromSession(AgentState state, DateTimeOffset occurredAt)
    {
        var historySlice = state.History.Skip(state.LastEpisodeHistoryIndex).ToList();
        if (historySlice.Count == 0)
            return null;

        var firstUser = historySlice.FirstOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        var title = (firstUser?.Content ?? historySlice[0].Content).Trim();
        if (title.Length == 0)
            title = $"Session with {state.AgentName}";
        if (title.Length > 100)
            title = title[..100];

        return new Episode
        {
            EpisodeId = Shared.Ids.EpisodeId.New(),
            Title = title,
            Narrative = BuildNarrative(historySlice, fallback: title),
            AgentName = state.AgentName,
            Tags = [state.AgentName],
            Decisions = [],
            SourceTaskId = null,
            SourceMessageIds = [],
            OccurredAt = occurredAt
        };
    }

    private static string BuildNarrative(List<ConversationMessage> messages, string fallback)
    {
        if (messages.Count == 0)
            return fallback;

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            var content = message.Content.Length > 500 ? message.Content[..500] : message.Content;
            if (string.IsNullOrWhiteSpace(content))
                continue;
            builder.Append(message.Role).Append(": ").AppendLine(content);
        }

        var narrative = builder.ToString().TrimEnd();
        return narrative.Length == 0 ? fallback : narrative;
    }

    private static List<EpisodeDecision> BuildDecisionsFromProof(ProofOfWork? proof)
    {
        if (proof is null || proof.Items.Count == 0)
            return [];

        var decisionTypes = new[] { ProofType.PullRequest, ProofType.CodeReview, ProofType.Custom };
        return proof.Items
            .Where(p => decisionTypes.Contains(p.Type))
            .Select(p => new EpisodeDecision
            {
                Question = $"{p.Type}: {p.Label}",
                ChosenOption = p.Value.Length > 200 ? p.Value[..200] : p.Value,
                Rationale = proof.ReviewFeedback
            })
            .ToList();
    }

    private static List<string> BuildTags(AgentTaskInfo task, AgentState state)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.AgentName };
        if (task.Proof is not null)
        {
            foreach (var item in task.Proof.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Label))
                    tags.Add(item.Label);
            }
        }
        return tags.ToList();
    }

    internal static SkillDocument? ExtractSkillFromTask(AgentTaskInfo task, AgentState state)
    {
        if (task.Proof is null || task.Proof.Items.Count < 2)
            return null;

        var toolsUsed = task.Proof.Items
            .Where(p => p.Type is ProofType.Custom or ProofType.DiffSummary)
            .Select(p => p.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var steps = task.Proof.Items.Select((item, index) => new SkillStep
        {
            Order = index,
            Action = $"{item.Type}: {item.Label}",
            ToolName = item.Type == ProofType.Custom ? item.Label : null,
            ExpectedOutcome = item.Value.Length > 200 ? item.Value[..200] : item.Value
        }).ToList();

        return new SkillDocument
        {
            SkillId = Shared.Ids.SkillId.New(),
            Title = task.Description.Length > 100 ? task.Description[..100] : task.Description,
            Description = $"Auto-extracted from completed task: {task.Description}",
            Tags = toolsUsed,
            Steps = steps,
            ToolsUsed = state.ConnectedTools.ToList(),
            CreatedByAgent = state.AgentName,
            OriginTaskDescription = task.Description
        };
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

        ApplyIdentity(_key, workspaceId);
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
}
