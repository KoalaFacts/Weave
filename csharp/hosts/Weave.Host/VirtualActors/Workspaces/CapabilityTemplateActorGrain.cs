using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.VirtualActors;

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
