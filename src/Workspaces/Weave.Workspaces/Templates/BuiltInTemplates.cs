using Weave.Shared.Ids;

using Weave.Workspaces.Manifest;
namespace Weave.Workspaces.Templates;

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

    public static readonly CapabilityTemplate Starter = new()
    {
        TemplateId = TemplateId.From("tpl-built-in-starter"),
        Name = "starter",
        Description = "One assistant, no tools — the simplest possible workspace.",
        Version = Version,
        Author = Author,
        AgentDefinition = new AgentDefinition { Model = Model },
        Tags = [.. Tags]
    };

    public static readonly CapabilityTemplate CodingAssistant = new()
    {
        TemplateId = TemplateId.From("tpl-built-in-coding-assistant"),
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
                Version = Version,
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
                Version = Version,
                FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
            }
        },
        Tags = [.. Tags]
    };

    public static readonly CapabilityTemplate Research = new()
    {
        TemplateId = TemplateId.From("tpl-built-in-research"),
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
                Version = Version,
                Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
            },
            ["files"] = new ToolDefinition
            {
                Type = "filesystem",
                Version = Version,
                FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
            }
        },
        Tags = [.. Tags]
    };

    public static readonly CapabilityTemplate MultiAgentSupervisor = new()
    {
        TemplateId = TemplateId.From("tpl-built-in-multi-agent-supervisor"),
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
                Version = Version,
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
                Version = Version,
                FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true }
            },
            ["web-search"] = new ToolDefinition
            {
                Type = "mcp",
                Version = Version,
                Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
            }
        },
        Tags = [.. Tags]
    };

    public static readonly CapabilityTemplate SupportBot = new()
    {
        TemplateId = TemplateId.From("tpl-built-in-support-bot"),
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
                Version = Version,
                Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/mcp-server-web-search"] }
            },
            ["files"] = new ToolDefinition
            {
                Type = "filesystem",
                Version = Version,
                FileSystem = new FileSystemToolConfig { Root = "./workspace-data", Sandbox = true, ReadOnly = true }
            }
        },
        Tags = [.. Tags]
    };

    public static readonly IReadOnlyList<CapabilityTemplate> All =
        [Starter, CodingAssistant, Research, MultiAgentSupervisor, SupportBot];
}
