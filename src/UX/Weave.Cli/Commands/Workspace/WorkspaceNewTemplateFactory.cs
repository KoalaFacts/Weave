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

        if (isSupportTeam)
            return CreateSupportTeam(selection);

        return isMultiAgent ? CreateMultiAgent(selection) : CreateSingleAgent(selection);
    }

    public static Dictionary<string, ToolDefinition> CreateTools(WorkspaceNewSelection selection)
    {
        var activePreset = selection.SelectedPresetName is not null && WorkspacePresets.All.TryGetValue(selection.SelectedPresetName, out var presetRef)
            ? presetRef
            : null;

        return activePreset?.ToolDefinitions is not null
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

    private static WorkspaceNewTemplate CreateSupportTeam(WorkspaceNewSelection selection)
    {
        var agents = new Dictionary<string, AgentDefinition>
        {
            ["support-bot"] = new AgentDefinition
            {
                Model = selection.Model,
                SystemPromptFile = "./prompts/support-bot.md",
                MaxConcurrentTasks = 5,
                Tools = selection.Tools
            },
            ["monitor"] = new AgentDefinition
            {
                Model = "claude-haiku-4-5-20251001",
                SystemPromptFile = "./prompts/monitor.md",
                MaxConcurrentTasks = 1,
                Tools = ["web-search"],
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

    private static WorkspaceNewTemplate CreateMultiAgent(WorkspaceNewSelection selection)
    {
        var agents = new Dictionary<string, AgentDefinition>
        {
            ["supervisor"] = new AgentDefinition
            {
                Model = selection.Model,
                SystemPromptFile = "./prompts/supervisor.md",
                MaxConcurrentTasks = 5,
                Tools = selection.Tools
            },
            ["worker"] = new AgentDefinition
            {
                Model = selection.Model,
                SystemPromptFile = "./prompts/worker.md",
                MaxConcurrentTasks = 3,
                Tools = selection.Tools
            }
        };

        return new WorkspaceNewTemplate(
            agents,
            [
                ("supervisor.md", "# Supervisor\n\nYou coordinate tasks across worker assistants. Break complex requests into subtasks and delegate them.\n"),
                ("worker.md", "# Worker\n\nYou execute tasks assigned by the supervisor. Focus on completing one task at a time with high quality.\n")
            ]);
    }

    private static WorkspaceNewTemplate CreateSingleAgent(WorkspaceNewSelection selection)
    {
        var agents = new Dictionary<string, AgentDefinition>
        {
            ["assistant"] = new AgentDefinition
            {
                Model = selection.Model,
                SystemPromptFile = "./prompts/assistant.md",
                MaxConcurrentTasks = 3,
                Tools = selection.Tools
            }
        };

        return new WorkspaceNewTemplate(agents, [("assistant.md", "# Assistant\n\nYou are a helpful AI assistant.\n")]);
    }
}
