namespace Weave.Workspaces.Models;

public sealed record TemplateRegistryState
{
    public Dictionary<string, CapabilityTemplate> Templates { get; init; } = [];
}
