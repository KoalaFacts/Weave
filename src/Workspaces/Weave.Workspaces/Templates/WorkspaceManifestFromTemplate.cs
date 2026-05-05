using Weave.Workspaces.Manifest;

namespace Weave.Workspaces.Templates;

/// <summary>
/// Composes a single-agent <see cref="WorkspaceManifest"/> from a curated
/// <see cref="CapabilityTemplate"/>. Shared by marketplace install and
/// workspace-new preset paths so they emit equivalent base manifests.
/// </summary>
public static class WorkspaceManifestFromTemplate
{
    public const string DefaultAgentName = "assistant";

    public static WorkspaceManifest Create(
        CapabilityTemplate template,
        string workspaceName,
        IsolationLevel isolation,
        string agentName = DefaultAgentName)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var primaryAgent = template.AgentDefinition with
        {
            SystemPromptFile = $"./prompts/{agentName}.md"
        };

        return new WorkspaceManifest
        {
            Version = "1.0",
            Name = workspaceName,
            Workspace = new WorkspaceConfig
            {
                Isolation = isolation,
                Network = new NetworkConfig { Name = $"weave-{workspaceName}" },
                Secrets = new SecretsConfig { Provider = "env" }
            },
            Agents = new Dictionary<string, AgentDefinition>
            {
                [agentName] = primaryAgent
            },
            Tools = new Dictionary<string, ToolDefinition>(template.RequiredTools),
            Targets = new Dictionary<string, TargetDefinition>
            {
                ["local"] = new TargetDefinition { Runtime = "podman" }
            }
        };
    }
}
