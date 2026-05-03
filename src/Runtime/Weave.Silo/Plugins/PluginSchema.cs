namespace Weave.Silo.Plugins;

public sealed record PluginSchema
{
    public required string Type { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Provides { get; init; }
    public required IReadOnlyList<PluginConfigField> Config { get; init; }
}
