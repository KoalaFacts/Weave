using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceNewTemplateFactory
{
    public static WorkspaceNewTemplate Create(WorkspaceNewSelection selection)
    {
        var activePreset = selection.SelectedPresetName is not null && WorkspacePresets.All.TryGetValue(selection.SelectedPresetName, out var presetRef)
            ? presetRef
            : null;
        var isMultiAgent = activePreset?.IsMultiAgent ?? false;
        var isSupportTeam = string.Equals(selection.SelectedPresetName, "support-team", StringComparison.OrdinalIgnoreCase);

        if (activePreset is null)
            return CreateCustom(selection);

        if (isSupportTeam)
            return CreateSupportTeam(activePreset);

        return isMultiAgent ? CreateMultiAgent(activePreset) : CreateSingleAgent(activePreset);
    }

    public static Dictionary<string, ToolDefinition> CreateTools(WorkspaceNewSelection selection)
    {
        var activePreset = selection.SelectedPresetName is not null && WorkspacePresets.All.TryGetValue(selection.SelectedPresetName, out var presetRef)
            ? presetRef
            : null;

        return activePreset is not null
            ? new Dictionary<string, ToolDefinition>(activePreset.ToolDefinitions)
            : selection.Tools.ToDictionary(t => t, _ => new ToolDefinition { Type = "mcp" });
    }

    public static Dictionary<string, ChannelDefinition> CreateChannels(WorkspaceNewSelection selection)
    {
        var activePreset = selection.SelectedPresetName is not null && WorkspacePresets.All.TryGetValue(selection.SelectedPresetName, out var presetRef)
            ? presetRef
            : null;

        return activePreset?.Channels is not null
            ? new Dictionary<string, ChannelDefinition>(activePreset.Channels)
            : [];
    }

    private static WorkspaceNewTemplate CreateSupportTeam(PresetDefinition preset)
    {
        // Primary agent ("support-bot") comes from the curated template via the
        // shared primitive; the monitor is a hard-coded composition extra.
        var manifest = WorkspaceManifestFromTemplate.Create(
            preset.PrimaryTemplate, "scratch", IsolationLevel.Full, agentName: "support-bot");

        var agents = new Dictionary<string, AgentDefinition>(manifest.Agents)
        {
            ["monitor"] = new AgentDefinition
            {
                Model = "claude-haiku-4-5-20251001",
                SystemPromptFile = "./prompts/monitor.md",
                MaxConcurrentTasks = 1,
                Tools = ["web-search"],
                Capabilities = ["tool:web-search"],
                Heartbeat = new HeartbeatConfig
                {
                    Cron = "*/5 * * * *",
                    Tasks = ["Check service health"]
                }
            }
        };

        return new WorkspaceNewTemplate(
            agents,
            [
                ("support-bot.md", "# Support Bot\n\nYou are a helpful support agent. Answer questions using available tools and your skill memory. Be concise and helpful.\n"),
                ("monitor.md", "# Monitor\n\nYou monitor service health. Report any issues you find.\n")
            ]);
    }

    private static WorkspaceNewTemplate CreateMultiAgent(PresetDefinition preset)
    {
        // Primary agent ("supervisor") comes from the curated template via the
        // shared primitive; the worker is a hard-coded composition extra that
        // mirrors the supervisor's tool/capability set.
        var manifest = WorkspaceManifestFromTemplate.Create(
            preset.PrimaryTemplate, "scratch", IsolationLevel.Full, agentName: "supervisor");

        var agents = new Dictionary<string, AgentDefinition>(manifest.Agents)
        {
            ["worker"] = new AgentDefinition
            {
                Model = preset.Model,
                SystemPromptFile = "./prompts/worker.md",
                MaxConcurrentTasks = 3,
                Tools = preset.Tools,
                Capabilities = preset.Capabilities
            }
        };

        return new WorkspaceNewTemplate(
            agents,
            [
                ("supervisor.md", "# Supervisor\n\nYou coordinate tasks across worker assistants. Break complex requests into subtasks and delegate them.\n"),
                ("worker.md", "# Worker\n\nYou execute tasks assigned by the supervisor. Focus on completing one task at a time with high quality.\n")
            ]);
    }

    private static WorkspaceNewTemplate CreateSingleAgent(PresetDefinition preset)
    {
        var manifest = WorkspaceManifestFromTemplate.Create(
            preset.PrimaryTemplate, "scratch", IsolationLevel.Full);

        return new WorkspaceNewTemplate(
            new Dictionary<string, AgentDefinition>(manifest.Agents),
            [("assistant.md", "# Assistant\n\nYou are a helpful AI assistant.\n")]);
    }

    private static WorkspaceNewTemplate CreateCustom(WorkspaceNewSelection selection)
    {
        // Custom flow (no preset). Builds an "assistant" agent directly from the
        // user's selection — no template to compose from.
        var agents = new Dictionary<string, AgentDefinition>
        {
            ["assistant"] = new AgentDefinition
            {
                Model = selection.Model,
                SystemPromptFile = "./prompts/assistant.md",
                MaxConcurrentTasks = 3,
                Tools = selection.Tools,
                Capabilities = selection.Capabilities
            }
        };

        return new WorkspaceNewTemplate(agents, [("assistant.md", "# Assistant\n\nYou are a helpful AI assistant.\n")]);
    }
}
