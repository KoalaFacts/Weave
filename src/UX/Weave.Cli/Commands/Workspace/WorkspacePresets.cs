using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspacePresets
{
    public static readonly IReadOnlyDictionary<string, PresetDefinition> All =
        new Dictionary<string, PresetDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["starter"] = new("starter",
                "One assistant, no tools — the simplest possible workspace.",
                "claude-sonnet-4-20250514", [],
                Capabilities: []),

            ["coding-assistant"] = new("coding-assistant",
                "An assistant with git and file tools, ready for code tasks.",
                "claude-sonnet-4-20250514", ["git", "files"],
                ToolDefinitions: new Dictionary<string, ToolDefinition>
                {
                    ["git"] = new()
                    {
                        Type = "cli",
                        Cli = new Weave.Workspaces.Models.CliConfig
                        {
                            Shell = "/bin/bash",
                            AllowedCommands = ["git *"],
                            DeniedCommands = ["git push --force", "git reset --hard"]
                        }
                    },
                    ["files"] = new()
                    {
                        Type = "filesystem",
                        FileSystem = new Weave.Workspaces.Models.FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
                    }
                },
                Capabilities: ["tool:git", "tool:files"]),

            ["research"] = new("research",
                "An assistant with web search and file tools for gathering information.",
                "claude-sonnet-4-20250514", ["web-search", "files"],
                ToolDefinitions: new Dictionary<string, ToolDefinition>
                {
                    ["web-search"] = new()
                    {
                        Type = "mcp",
                        Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
                    },
                    ["files"] = new()
                    {
                        Type = "filesystem",
                        FileSystem = new Weave.Workspaces.Models.FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
                    }
                },
                Capabilities: ["tool:web-search", "tool:files"]),

            ["multi-agent"] = new("multi-agent",
                "A supervisor and worker assistants for complex workflows.",
                "claude-sonnet-4-20250514", ["git", "files", "web-search"],
                ToolDefinitions: new Dictionary<string, ToolDefinition>
                {
                    ["git"] = new()
                    {
                        Type = "cli",
                        Cli = new Weave.Workspaces.Models.CliConfig
                        {
                            Shell = "/bin/bash",
                            AllowedCommands = ["git *"],
                            DeniedCommands = ["git push --force", "git reset --hard"]
                        }
                    },
                    ["files"] = new()
                    {
                        Type = "filesystem",
                        FileSystem = new Weave.Workspaces.Models.FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
                    },
                    ["web-search"] = new()
                    {
                        Type = "mcp",
                        Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
                    }
                },
                IsMultiAgent: true,
                Capabilities: ["tool:git", "tool:files", "tool:web-search"]),

            ["support-team"] = new("support-team",
                "A support bot on Slack with skill memory, user modeling, and a health monitor.",
                "claude-sonnet-4-20250514", ["web-search", "files"],
                ToolDefinitions: new Dictionary<string, ToolDefinition>
                {
                    ["web-search"] = new()
                    {
                        Type = "mcp",
                        Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
                    },
                    ["files"] = new()
                    {
                        Type = "filesystem",
                        FileSystem = new Weave.Workspaces.Models.FileSystemToolConfig { Root = "./workspace-data", Sandbox = true, ReadOnly = true }
                    }
                },
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
                },
                // Capabilities here describe the primary agent (support-bot). The
                // health-monitor agent's capabilities are hard-coded in the factory
                // alongside its hard-coded tool list — see
                // WorkspaceNewTemplateFactory.CreateSupportTeam.
                Capabilities: [
                    "tool:web-search",
                    "tool:files",
                    "channel:send:slack",
                    "channel:receive:slack",
                    "skill:read",
                    "skill:write",
                    "user:read:*",
                    "user:write:*"
                ]),
        };
}
