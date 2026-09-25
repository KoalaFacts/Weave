namespace Weave.Silo.Plugins;

public sealed record PluginCompositionEntry
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required IReadOnlyList<string> Provides { get; init; }
    public required IReadOnlyList<string> Registrations { get; init; }
    public required bool IsConnected { get; init; }
}
