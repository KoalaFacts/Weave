using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed record WorkspaceNewTemplate(
    Dictionary<string, AgentDefinition> Agents,
    List<(string FileName, string Content)> PromptFiles);
