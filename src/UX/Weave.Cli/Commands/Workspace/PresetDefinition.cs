using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed record PresetDefinition(
    string Name,
    string Description,
    string Model,
    IReadOnlyList<string> Tools,
    IReadOnlyDictionary<string, ToolDefinition>? ToolDefinitions = null,
    IReadOnlyDictionary<string, ChannelDefinition>? Channels = null,
    bool IsMultiAgent = false,
    IReadOnlyList<string>? Capabilities = null);
