using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class AgentGrain(
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<AgentGrain> logger) : Grain, IAgentGrain
{
    private AgentState _state = new() { AgentId = "", WorkspaceId = WorkspaceId.Empty, AgentName = "" };
    private AgentDefinition? _definition;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var key = this.GetPrimaryKeyString();
        var parts = key.Split('/', 2);
        _state = new AgentState
        {
            AgentId = key,
            WorkspaceId = WorkspaceId.From(parts.Length > 1 ? parts[0] : key),
            AgentName = parts.Length > 1 ? parts[1] : key
        };
        return Task.CompletedTask;
    }

    public async Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition)
    {
        if (_state.Status is AgentStatus.Active or AgentStatus.Busy)
            return _state;

        if (string.IsNullOrEmpty(_state.AgentId))
        {
            _state = new AgentState
            {
                AgentId = $"{workspaceId}/agent",
                WorkspaceId = workspaceId,
                AgentName = "agent"
            };
        }

        _definition = definition;
        _state.Status = AgentStatus.Activating;
        _state.Model = definition.Model;
        _state.MaxConcurrentTasks = definition.MaxConcurrentTasks;

        var context = new LifecycleContext
        {
            WorkspaceId = workspaceId,
            AgentName = _state.AgentName,
            Phase = LifecyclePhase.AgentActivating
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentActivating, context, CancellationToken.None);

            _state.Status = AgentStatus.Active;
            _state.ActivatedAt = DateTimeOffset.UtcNow;
            _state.DeactivatedAt = null;
            _state.ErrorMessage = null;

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.AgentActivated,
                context with { Phase = LifecyclePhase.AgentActivated },
                CancellationToken.None);

            await eventBus.PublishAsync(new AgentActivatedEvent
            {
                SourceId = _state.AgentId,
                AgentName = _state.AgentName,
                WorkspaceId = workspaceId,
                Model = definition.Model,
                Tools = definition.Tools
            }, CancellationToken.None);

            logger.LogInformation("Agent {AgentName} activated in workspace {WorkspaceId}",
                _state.AgentName, workspaceId);
        }
        catch (Exception ex)
        {
            _state.Status = AgentStatus.Error;
            _state.ErrorMessage = ex.Message;

            await eventBus.PublishAsync(new AgentErrorEvent
            {
                SourceId = _state.AgentId,
                AgentName = _state.AgentName,
                WorkspaceId = workspaceId,
                ErrorMessage = ex.Message
            }, CancellationToken.None);

            logger.LogError(ex, "Failed to activate agent {AgentName}", _state.AgentName);
            throw;
        }

        return _state;
    }

    public async Task DeactivateAsync()
    {
        if (_state.Status is AgentStatus.Idle or AgentStatus.Deactivating)
            return;

        _state.Status = AgentStatus.Deactivating;

        var context = new LifecycleContext
        {
            WorkspaceId = _state.WorkspaceId,
            AgentName = _state.AgentName,
            Phase = LifecyclePhase.AgentDeactivating
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.AgentDeactivating, context, CancellationToken.None);

            _state.Status = AgentStatus.Idle;
            _state.DeactivatedAt = DateTimeOffset.UtcNow;
            _state.ActiveTasks.Clear();
            _state.ConnectedTools.Clear();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.AgentDeactivated,
                context with { Phase = LifecyclePhase.AgentDeactivated },
                CancellationToken.None);

            await eventBus.PublishAsync(new AgentDeactivatedEvent
            {
                SourceId = _state.AgentId,
                AgentName = _state.AgentName,
                WorkspaceId = _state.WorkspaceId
            }, CancellationToken.None);

            logger.LogInformation("Agent {AgentName} deactivated", _state.AgentName);
        }
        catch (Exception ex)
        {
            _state.Status = AgentStatus.Error;
            _state.ErrorMessage = ex.Message;
            logger.LogError(ex, "Failed to deactivate agent {AgentName}", _state.AgentName);
            throw;
        }
    }

    public Task<AgentState> GetStateAsync() => Task.FromResult(_state);

    public Task<AgentTaskInfo> SubmitTaskAsync(string description)
    {
        if (_state.Status is not (AgentStatus.Active or AgentStatus.Busy))
            throw new InvalidOperationException($"Agent {_state.AgentName} is not active (status: {_state.Status}).");

        var runningCount = 0;
        foreach (var t in _state.ActiveTasks)
        {
            if (t.Status is AgentTaskStatus.Running)
                runningCount++;
        }
        if (runningCount >= _state.MaxConcurrentTasks)
            throw new InvalidOperationException(
                $"Agent {_state.AgentName} has reached max concurrent tasks ({_state.MaxConcurrentTasks}).");

        var task = new AgentTaskInfo
        {
            TaskId = AgentTaskId.New(),
            Description = description,
            Status = AgentTaskStatus.Running
        };

        _state.ActiveTasks.Add(task);
        _state.Status = AgentStatus.Busy;

        logger.LogInformation("Task {TaskId} submitted to agent {AgentName}", task.TaskId, _state.AgentName);
        return Task.FromResult(task);
    }

    public async Task CompleteTaskAsync(AgentTaskId taskId, bool success)
    {
        var taskIndex = _state.ActiveTasks.FindIndex(t => t.TaskId == taskId);
        if (taskIndex < 0)
            throw new InvalidOperationException($"Task {taskId} not found on agent {_state.AgentName}.");

        var completed = _state.ActiveTasks[taskIndex] with
        {
            Status = success ? AgentTaskStatus.Completed : AgentTaskStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _state.ActiveTasks[taskIndex] = completed;

        var hasRunning = false;
        foreach (var t in _state.ActiveTasks)
        {
            if (t.Status is AgentTaskStatus.Running)
            {
                hasRunning = true;
                break;
            }
        }
        if (!hasRunning)
            _state.Status = AgentStatus.Active;

        await eventBus.PublishAsync(new AgentTaskCompletedEvent
        {
            SourceId = _state.AgentId,
            AgentName = _state.AgentName,
            WorkspaceId = _state.WorkspaceId,
            TaskId = taskId
        }, CancellationToken.None);

        logger.LogInformation("Task {TaskId} completed on agent {AgentName} (success: {Success})",
            taskId, _state.AgentName, success);
    }

    public Task ConnectToolAsync(string toolName)
    {
        if (!_state.ConnectedTools.Contains(toolName))
            _state.ConnectedTools.Add(toolName);
        return Task.CompletedTask;
    }

    public Task DisconnectToolAsync(string toolName)
    {
        _state.ConnectedTools.Remove(toolName);
        return Task.CompletedTask;
    }
}
