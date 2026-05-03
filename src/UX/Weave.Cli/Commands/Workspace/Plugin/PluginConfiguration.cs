namespace Weave.Cli.Commands;

internal sealed record PluginConfiguration(string? Description, Dictionary<string, string> Config);
