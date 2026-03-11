using Microsoft.Extensions.Logging;
using Orleans;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Events;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Grains;

public sealed class WorkspaceGrain(
    IWorkspaceRuntime runtime,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<WorkspaceGrain> logger) : Grain, IWorkspaceGrain
{
    private WorkspaceState _state = null!;
    private WorkspaceManifest? _manifest;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _state = new WorkspaceState { WorkspaceId = this.GetPrimaryKeyString() };
        return Task.CompletedTask;
    }

    public async Task<WorkspaceState> StartAsync(WorkspaceManifest manifest)
    {
        if (_state.Status is WorkspaceStatus.Running)
            return _state;

        _manifest = manifest;
        _state.Status = WorkspaceStatus.Starting;

        var context = new LifecycleContext
        {
            WorkspaceId = _state.WorkspaceId,
            Phase = LifecyclePhase.WorkspaceStarting
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, context, CancellationToken.None);

            var env = await runtime.ProvisionAsync(manifest, CancellationToken.None);

            _state.Status = WorkspaceStatus.Running;
            _state.StartedAt = DateTimeOffset.UtcNow;
            _state.NetworkId = env.NetworkId;
            _state.Containers.Clear();
            _state.Containers.AddRange(env.Containers.Select(c => new ContainerInfo
            {
                ContainerId = c.ContainerId,
                Name = c.Name,
                Image = c.Image,
                Status = Models.ContainerStatus.Running
            }));

            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStarted, context with { Phase = LifecyclePhase.WorkspaceStarted }, CancellationToken.None);

            await eventBus.PublishAsync(new WorkspaceStartedEvent
            {
                SourceId = _state.WorkspaceId,
                WorkspaceName = manifest.Name,
                AgentNames = manifest.Agents.Keys.ToList()
            }, CancellationToken.None);

            logger.LogInformation("Workspace {WorkspaceId} started", _state.WorkspaceId);
        }
        catch (Exception ex)
        {
            _state.Status = WorkspaceStatus.Error;
            _state.ErrorMessage = ex.Message;
            logger.LogError(ex, "Failed to start workspace {WorkspaceId}", _state.WorkspaceId);
            throw;
        }

        return _state;
    }

    public async Task StopAsync()
    {
        if (_state.Status is not WorkspaceStatus.Running)
            return;

        _state.Status = WorkspaceStatus.Stopping;

        var context = new LifecycleContext
        {
            WorkspaceId = _state.WorkspaceId,
            Phase = LifecyclePhase.WorkspaceStopping
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStopping, context, CancellationToken.None);
            await runtime.TeardownAsync(_state.WorkspaceId, CancellationToken.None);

            _state.Status = WorkspaceStatus.Stopped;
            _state.StoppedAt = DateTimeOffset.UtcNow;
            _state.Containers.Clear();
            _state.NetworkId = null;

            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStopped, context with { Phase = LifecyclePhase.WorkspaceStopped }, CancellationToken.None);

            await eventBus.PublishAsync(new WorkspaceStoppedEvent
            {
                SourceId = _state.WorkspaceId
            }, CancellationToken.None);

            logger.LogInformation("Workspace {WorkspaceId} stopped", _state.WorkspaceId);
        }
        catch (Exception ex)
        {
            _state.Status = WorkspaceStatus.Error;
            _state.ErrorMessage = ex.Message;
            logger.LogError(ex, "Failed to stop workspace {WorkspaceId}", _state.WorkspaceId);
            throw;
        }
    }

    public Task<WorkspaceState> GetStateAsync() => Task.FromResult(_state);
}
