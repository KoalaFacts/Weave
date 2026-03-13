using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Events;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Grains;

public sealed partial class WorkspaceGrain(
    IWorkspaceRuntime runtime,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<WorkspaceGrain> logger,
    [PersistentState("workspace", "Default")] IPersistentState<WorkspaceState> persistentState) : Grain, IWorkspaceGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await persistentState.ReadStateAsync(cancellationToken);
        var key = TryGetPrimaryKeyString();
        if (persistentState.State.WorkspaceId.IsEmpty && !string.IsNullOrWhiteSpace(key))
        {
            persistentState.State.WorkspaceId = WorkspaceId.From(key!);
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task<WorkspaceState> StartAsync(WorkspaceManifest manifest)
    {
        if (persistentState.State.Status is WorkspaceStatus.Running)
            return persistentState.State;

        persistentState.State.Status = WorkspaceStatus.Starting;

        var context = new LifecycleContext
        {
            WorkspaceId = persistentState.State.WorkspaceId,
            Phase = LifecyclePhase.WorkspaceStarting
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, context, CancellationToken.None);

            var env = await runtime.ProvisionAsync(manifest, CancellationToken.None);

            persistentState.State.Status = WorkspaceStatus.Running;
            persistentState.State.StartedAt = DateTimeOffset.UtcNow;
            persistentState.State.NetworkId = env.NetworkId;
            persistentState.State.Containers.Clear();
            persistentState.State.ActiveAgents.Clear();
            persistentState.State.ActiveTools.Clear();
            persistentState.State.ActiveAgents.AddRange(manifest.Agents.Keys);
            persistentState.State.ActiveTools.AddRange(manifest.Tools.Keys);
            foreach (var container in env.Containers)
            {
                persistentState.State.Containers.Add(new ContainerInfo
                {
                    ContainerId = container.ContainerId,
                    Name = container.Name,
                    Image = container.Image,
                    Status = Models.ContainerStatus.Running
                });
            }

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.WorkspaceStarted,
                context with { Phase = LifecyclePhase.WorkspaceStarted },
                CancellationToken.None);

            await eventBus.PublishAsync(new WorkspaceStartedEvent
            {
                SourceId = persistentState.State.WorkspaceId,
                WorkspaceName = manifest.Name,
                AgentNames = [.. manifest.Agents.Keys]
            }, CancellationToken.None);

            LogWorkspaceStarted(persistentState.State.WorkspaceId);
        }
        catch (Exception ex)
        {
            persistentState.State.Status = WorkspaceStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();
            LogWorkspaceFailed(ex, persistentState.State.WorkspaceId, "start");
            throw;
        }

        return persistentState.State;
    }

    public async Task StopAsync()
    {
        if (persistentState.State.Status is not WorkspaceStatus.Running)
            return;

        persistentState.State.Status = WorkspaceStatus.Stopping;

        var context = new LifecycleContext
        {
            WorkspaceId = persistentState.State.WorkspaceId,
            Phase = LifecyclePhase.WorkspaceStopping
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStopping, context, CancellationToken.None);
            await runtime.TeardownAsync(persistentState.State.WorkspaceId, CancellationToken.None);

            persistentState.State.Status = WorkspaceStatus.Stopped;
            persistentState.State.StoppedAt = DateTimeOffset.UtcNow;
            persistentState.State.Containers.Clear();
            persistentState.State.ActiveAgents.Clear();
            persistentState.State.ActiveTools.Clear();
            persistentState.State.NetworkId = null;

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.WorkspaceStopped,
                context with { Phase = LifecyclePhase.WorkspaceStopped },
                CancellationToken.None);

            await eventBus.PublishAsync(new WorkspaceStoppedEvent
            {
                SourceId = persistentState.State.WorkspaceId
            }, CancellationToken.None);

            LogWorkspaceStopped(persistentState.State.WorkspaceId);
        }
        catch (Exception ex)
        {
            persistentState.State.Status = WorkspaceStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();
            LogWorkspaceFailed(ex, persistentState.State.WorkspaceId, "stop");
            throw;
        }
    }

    public Task<WorkspaceState> GetStateAsync() => Task.FromResult(persistentState.State);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} started")]
    private partial void LogWorkspaceStarted(WorkspaceId workspaceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} stopped")]
    private partial void LogWorkspaceStopped(WorkspaceId workspaceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to {Operation} workspace {WorkspaceId}")]
    private partial void LogWorkspaceFailed(Exception ex, WorkspaceId workspaceId, string operation);
}
