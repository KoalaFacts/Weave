using Weave.Shared.Ids;

namespace Weave.Workspaces.Models;

/// <summary>
/// Curated capability-template starter set seeded into the runtime registry
/// at silo startup. One entry per primary agent shape; secondary agents
/// (multi-agent workers, support-team monitor) live in CLI preset definitions.
/// </summary>
public static class BuiltInTemplates
{
    public const string Author = "weave";
    public const string Version = "1.0.0";
    public static readonly IReadOnlyList<string> Tags = ["built-in", "weave"];
    private const string Model = "claude-sonnet-4-20250514";

    public static readonly TemplateId Starter = TemplateId.From("tpl-built-in-starter");
    public static readonly TemplateId CodingAssistant = TemplateId.From("tpl-built-in-coding-assistant");
    public static readonly TemplateId Research = TemplateId.From("tpl-built-in-research");
    public static readonly TemplateId MultiAgentSupervisor = TemplateId.From("tpl-built-in-multi-agent-supervisor");
    public static readonly TemplateId SupportBot = TemplateId.From("tpl-built-in-support-bot");

    public static readonly IReadOnlyList<CapabilityTemplate> All =
    [
        new()
        {
            TemplateId = Starter,
            Name = "starter",
            Description = "One assistant, no tools — the simplest possible workspace.",
            Version = Version,
            Author = Author,
            AgentDefinition = new AgentDefinition { Model = Model },
            Tags = [.. Tags]
        },
        new()
        {
            TemplateId = CodingAssistant,
            Name = "coding-assistant",
            Description = "An assistant with git and file tools, ready for code tasks.",
            Version = Version,
            Author = Author,
            AgentDefinition = new AgentDefinition
            {
                Model = Model,
                Tools = ["git", "files"],
                Capabilities = ["tool:git", "tool:files"]
            },
            RequiredTools =
            {
                ["git"] = new ToolDefinition
                {
                    Type = "cli",
                    Cli = new CliConfig
                    {
                        Shell = "/bin/bash",
                        AllowedCommands = ["git *"],
                        DeniedCommands = ["git push --force", "git reset --hard"]
                    }
                },
                ["files"] = new ToolDefinition
                {
                    Type = "filesystem",
                    FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
                }
            },
            Tags = [.. Tags]
        },
        new()
        {
            TemplateId = Research,
            Name = "research",
            Description = "An assistant with web search and file tools for gathering information.",
            Version = Version,
            Author = Author,
            AgentDefinition = new AgentDefinition
            {
                Model = Model,
                Tools = ["web-search", "files"],
                Capabilities = ["tool:web-search", "tool:files"]
            },
            RequiredTools =
            {
                ["web-search"] = new ToolDefinition
                {
                    Type = "mcp",
                    Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
                },
                ["files"] = new ToolDefinition
                {
                    Type = "filesystem",
                    FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
                }
            },
            Tags = [.. Tags]
        },
        new()
        {
            TemplateId = MultiAgentSupervisor,
            Name = "multi-agent-supervisor",
            Description = "Supervisor for the multi-agent preset — coordinates worker assistants across git, file, and web tools.",
            Version = Version,
            Author = Author,
            AgentDefinition = new AgentDefinition
            {
                Model = Model,
                Tools = ["git", "files", "web-search"],
                Capabilities = ["tool:git", "tool:files", "tool:web-search"]
            },
            RequiredTools =
            {
                ["git"] = new ToolDefinition
                {
                    Type = "cli",
                    Cli = new CliConfig
                    {
                        Shell = "/bin/bash",
                        AllowedCommands = ["git *"],
                        DeniedCommands = ["git push --force", "git reset --hard"]
                    }
                },
                ["files"] = new ToolDefinition
                {
                    Type = "filesystem",
                    FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
                },
                ["web-search"] = new ToolDefinition
                {
                    Type = "mcp",
                    Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
                }
            },
            Tags = [.. Tags]
        },
        new()
        {
            TemplateId = SupportBot,
            Name = "support-bot",
            Description = "Slack-fronted support agent with skill memory, user modeling, and read-only file access.",
            Version = Version,
            Author = Author,
            AgentDefinition = new AgentDefinition
            {
                Model = Model,
                Tools = ["web-search", "files"],
                Capabilities =
                [
                    "tool:web-search",
                    "tool:files",
                    "channel:send:slack",
                    "channel:receive:slack",
                    "skill:read",
                    "skill:write",
                    "user:read:*",
                    "user:write:*"
                ]
            },
            RequiredTools =
            {
                ["web-search"] = new ToolDefinition
                {
                    Type = "mcp",
                    Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
                },
                ["files"] = new ToolDefinition
                {
                    Type = "filesystem",
                    FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true, ReadOnly = true }
                }
            },
            Tags = [.. Tags]
        }
    ];
}
