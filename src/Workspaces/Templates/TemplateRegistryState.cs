namespace Weave.Workspaces.Templates;

public sealed record TemplateRegistryState
{
    public Dictionary<string, CapabilityTemplate> Templates { get; init; } = [];
}
