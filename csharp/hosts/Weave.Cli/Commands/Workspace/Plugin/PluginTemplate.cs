namespace Weave.Cli.Commands;

internal sealed record PluginTemplate(string Type, string Description, Dictionary<string, string> DefaultConfig);
