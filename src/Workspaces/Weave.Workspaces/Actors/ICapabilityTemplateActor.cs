using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Actors;

public interface ICapabilityTemplateActor : IVirtualActorWithStringKey
{
    Task<CapabilityTemplate> RegisterAsync(CapabilityTemplate template);
    Task<CapabilityTemplate> ValidateAndPublishAsync(TemplateId templateId);
    Task<CapabilityTemplate?> GetAsync(TemplateId templateId);
    Task<IReadOnlyList<CapabilityTemplate>> ListPublishedAsync(int offset = 0, int limit = 50);
    Task<IReadOnlyList<CapabilityTemplate>> SearchAsync(string? query, int maxResults = 20);
    Task DeprecateAsync(TemplateId templateId);
    Task IncrementInstantiationCountAsync(TemplateId templateId);
}
