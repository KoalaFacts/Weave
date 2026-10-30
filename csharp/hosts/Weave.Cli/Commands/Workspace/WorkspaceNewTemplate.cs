using Weave.Workspaces.Manifest;
namespace Weave.Cli.Commands;

internal sealed record WorkspaceNewTemplate(
    Dictionary<string, AgentDefinition> Agents,
    List<(string FileName, string Content)> PromptFiles);
