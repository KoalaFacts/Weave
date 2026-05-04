using Microsoft.Extensions.Logging;
using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Lifecycle;

internal sealed class AgentLifecycle(
    IAgentChatPipeline chatPipeline,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    TimeProvider timeProvider,
    AgentEpisodeRecorder episodeRecorder,
    ILogger logger,
    IActorState<AgentState> persistentState)
{
    public async Task<AgentState> ActivateAsync(WorkspaceId workspaceId, AgentDefinition definition)
    {
        var state = persistentState.State;
        state.Status = AgentStatus.Activating;
        state.Model = definition.Model;
        state.MaxConcurrentTasks = definition.MaxConcurrentTasks;
        state.Definition = definition;

        var context = new LifecycleContext
        {
            WorkspaceId = workspaceId,
            AgentName = state.AgentName,
            Phase = LifecyclePhase.AgentActivating
        };

        await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentActivating, context, CancellationToken.None);

        chatPipeline.Reset();
        chatPipeline.Initialize(state.AgentId, definition.Model);

        state.Status = AgentStatus.Active;
        state.ActivatedAt = timeProvider.GetUtcNow();
        state.DeactivatedAt = null;
        state.ErrorMessage = null;
        state.LastActive = state.ActivatedAt;

        await persistentState.WriteStateAsync();

        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.AgentActivated,
            context with { Phase = LifecyclePhase.AgentActivated },
            CancellationToken.None);

        await eventBus.PublishAsync(new AgentActivatedEvent
        {
            SourceId = state.AgentId,
            AgentName = state.AgentName,
            WorkspaceId = workspaceId,
            Model = definition.Model,
            Tools = definition.Tools
        }, CancellationToken.None);

        logger.LogInformation(
            "Agent {AgentName} activated in workspace {WorkspaceId}",
            state.AgentName,
            workspaceId);

        return state;
    }

    public async Task DeactivateAsync()
    {
        var state = persistentState.State;
        state.Status = AgentStatus.Deactivating;

        var context = new LifecycleContext
        {
            WorkspaceId = state.WorkspaceId,
            AgentName = state.AgentName,
            Phase = LifecyclePhase.AgentDeactivating
        };

        await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentDeactivating, context, CancellationToken.None);

        chatPipeline.Reset();

        await episodeRecorder.StoreSessionEpisodeAsync(state);

        state.Status = AgentStatus.Idle;
        state.DeactivatedAt = timeProvider.GetUtcNow();
        state.ActiveTasks.Clear();
        state.ConnectedTools.Clear();

        await persistentState.WriteStateAsync();

        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.AgentDeactivated,
            context with { Phase = LifecyclePhase.AgentDeactivated },
            CancellationToken.None);

        await eventBus.PublishAsync(new AgentDeactivatedEvent
        {
            SourceId = state.AgentId,
            AgentName = state.AgentName,
            WorkspaceId = state.WorkspaceId
        }, CancellationToken.None);

        logger.LogInformation("Agent {AgentName} deactivated", state.AgentName);
    }
}
