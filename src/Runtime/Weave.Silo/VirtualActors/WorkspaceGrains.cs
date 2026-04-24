using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Shared.VirtualActors;
using Weave.Workspaces.Actors;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Silo.VirtualActors;

public sealed class WorkspaceActorGrain : Grain, IWorkspaceActorGrain
{
    private readonly WorkspaceActor _actor;

    public WorkspaceActorGrain(
        IWorkspaceRuntime runtime,
        ILifecycleManager lifecycleManager,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<WorkspaceActor> logger,
        [PersistentState("workspace", "Default")] IPersistentState<WorkspaceState> state)
    {
        _actor = new WorkspaceActor(runtime, lifecycleManager, eventBus, timeProvider, logger,
            new OrleansActorState<WorkspaceState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<WorkspaceState> StartAsync(WorkspaceManifest manifest) => _actor.StartAsync(manifest);
    public Task StopAsync() => _actor.StopAsync();
    public Task<WorkspaceState> GetStateAsync() => _actor.GetStateAsync();
}

public sealed class WorkspaceRegistryActorGrain : Grain, IWorkspaceRegistryActorGrain
{
    private readonly WorkspaceRegistryActor _actor;

    public WorkspaceRegistryActorGrain(
        [PersistentState("workspace-registry", "Default")] IPersistentState<WorkspaceRegistryState> state)
    {
        _actor = new WorkspaceRegistryActor(new OrleansActorState<WorkspaceRegistryState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RegisterAsync(string workspaceId) => _actor.RegisterAsync(workspaceId);
    public Task UnregisterAsync(string workspaceId) => _actor.UnregisterAsync(workspaceId);
    public Task<IReadOnlyList<string>> GetWorkspaceIdsAsync() => _actor.GetWorkspaceIdsAsync();
}

public sealed class CapabilityTemplateActorGrain : Grain, ICapabilityTemplateActorGrain
{
    private readonly CapabilityTemplateActor _actor;

    public CapabilityTemplateActorGrain(
        TimeProvider timeProvider,
        ILogger<CapabilityTemplateActor> logger,
        [PersistentState("capability-templates", "Default")] IPersistentState<TemplateRegistryState> state)
    {
        _actor = new CapabilityTemplateActor(timeProvider, logger,
            new OrleansActorState<TemplateRegistryState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<CapabilityTemplate> RegisterAsync(CapabilityTemplate template) => _actor.RegisterAsync(template);

    public Task<CapabilityTemplate> ValidateAndPublishAsync(TemplateId templateId) =>
        _actor.ValidateAndPublishAsync(templateId);

    public Task<CapabilityTemplate?> GetAsync(TemplateId templateId) => _actor.GetAsync(templateId);

    public Task<IReadOnlyList<CapabilityTemplate>> ListPublishedAsync(int offset = 0, int limit = 50) =>
        _actor.ListPublishedAsync(offset, limit);

    public Task<IReadOnlyList<CapabilityTemplate>> SearchAsync(string? query, int maxResults = 20) =>
        _actor.SearchAsync(query, maxResults);

    public Task DeprecateAsync(TemplateId templateId) => _actor.DeprecateAsync(templateId);
    public Task IncrementInstantiationCountAsync(TemplateId templateId) => _actor.IncrementInstantiationCountAsync(templateId);
}
