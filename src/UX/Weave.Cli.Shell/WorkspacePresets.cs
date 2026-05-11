using Weave.Workspaces.Manifest;
using Weave.Workspaces.Templates;
namespace Weave.Cli.Shell;

internal static class WorkspacePresets
{
    public static readonly IReadOnlyDictionary<string, PresetDefinition> All =
        new Dictionary<string, PresetDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["starter"] = new(
                Name: "starter",
                Description: BuiltInTemplates.Starter.Description,
                PrimaryTemplate: BuiltInTemplates.Starter),

            ["coding-assistant"] = new(
                Name: "coding-assistant",
                Description: BuiltInTemplates.CodingAssistant.Description,
                PrimaryTemplate: BuiltInTemplates.CodingAssistant),

            ["research"] = new(
                Name: "research",
                Description: BuiltInTemplates.Research.Description,
                PrimaryTemplate: BuiltInTemplates.Research),

            ["multi-agent"] = new(
                Name: "multi-agent",
                Description: "A supervisor and worker assistants for complex workflows.",
                PrimaryTemplate: BuiltInTemplates.MultiAgentSupervisor,
                IsMultiAgent: true),

            ["support-team"] = new(
                Name: "support-team",
                Description: "A support bot on Slack with skill memory, user modeling, and a health monitor.",
                PrimaryTemplate: BuiltInTemplates.SupportBot,
                Channels: new Dictionary<string, ChannelDefinition>
                {
                    ["slack"] = new()
                    {
                        Type = "slack",
                        TargetAgent = "support-bot",
                        Config = new Dictionary<string, string>
                        {
                            ["webhook_url"] = "https://hooks.slack.com/services/YOUR/WEBHOOK/URL"
                        }
                    }
                }),
        };
}
