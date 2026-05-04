using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

/// <summary>
/// CLI wrapper around a curated <see cref="CapabilityTemplate"/>. The template
/// is the source of agent shape; the preset adds composition extras (channels,
/// multi-agent flag).
/// </summary>
internal sealed record PresetDefinition(
    string Name,
    string Description,
    CapabilityTemplate PrimaryTemplate,
    IReadOnlyDictionary<string, ChannelDefinition>? Channels = null,
    bool IsMultiAgent = false)
{
    public string Model => PrimaryTemplate.AgentDefinition.Model;
    public IReadOnlyList<string> Tools => PrimaryTemplate.AgentDefinition.Tools;
    public IReadOnlyDictionary<string, ToolDefinition> ToolDefinitions => PrimaryTemplate.RequiredTools;
    public IReadOnlyList<string> Capabilities => PrimaryTemplate.AgentDefinition.Capabilities;
}
