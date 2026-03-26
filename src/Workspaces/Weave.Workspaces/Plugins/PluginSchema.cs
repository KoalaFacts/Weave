namespace Weave.Workspaces.Plugins;

/// <summary>
/// Self-describing schema for a built-in plugin type. Declares what the plugin
/// provides, what config it requires, and how to auto-detect from the environment.
/// Returned by <see cref="IPluginConnector.Schema"/>.
/// </summary>
[GenerateSerializer]
public sealed record PluginSchema
{
    [Id(0)] public required string Type { get; init; }
    [Id(1)] public required string Description { get; init; }
    [Id(2)] public required IReadOnlyList<string> Provides { get; init; }
    [Id(3)] public required IReadOnlyList<PluginConfigField> Config { get; init; }
}

/// <summary>
/// Describes a single configuration field for a plugin.
/// </summary>
[GenerateSerializer]
public sealed record PluginConfigField
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public required string Description { get; init; }
    [Id(2)] public bool Required { get; init; }
    [Id(3)] public bool Secret { get; init; }
    [Id(4)] public string? Default { get; init; }
    [Id(5)] public string? EnvVar { get; init; }
}
