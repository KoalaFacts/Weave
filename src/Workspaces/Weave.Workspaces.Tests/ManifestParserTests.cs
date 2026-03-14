using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Tests;

public sealed class ManifestParserTests
{
    private readonly ManifestParser _parser = new();

    private const string FullManifest = """
        version: "1.0"
        name: test-workspace

        workspace:
          isolation: full
          network:
            name: weave-test
            subnet: "10.42.0.0/16"
          filesystem:
            root: /var/weave/workspaces/test
            mounts:
              - source: ./data
                target: /workspace/data
                readonly: false
          secrets:
            provider: vault
            vault:
              address: https://vault.example.com
              mount: weave/test

        agents:
          researcher:
            model: claude-sonnet-4-20250514
            system_prompt_file: ./prompts/researcher.md
            max_concurrent_tasks: 5
            memory:
              provider: redis
              ttl: 24h
            tools:
              - web-search
            capabilities:
              - net:outbound
            heartbeat:
              cron: "*/30 * * * *"
              tasks:
                - Check for updates

        tools:
          web-search:
            type: mcp
            mcp:
              server: npx
              args:
                - "-y"
                - "@anthropic/mcp-server-web-search"
              env:
                ANTHROPIC_API_KEY: "${secrets.anthropic_api_key}"
          terminal:
            type: cli
            cli:
              shell: /bin/bash
              allowed_commands:
                - "git *"
              denied_commands:
                - "rm -rf /"

        targets:
          local:
            runtime: podman
          staging:
            runtime: k3s
            replicas: 2
        """;

    [Fact]
    public void Parse_FullManifest_ReturnsCorrectModel()
    {
        var manifest = _parser.Parse(FullManifest);

        manifest.Version.ShouldBe("1.0");
        manifest.Name.ShouldBe("test-workspace");
        manifest.Workspace.Isolation.ShouldBe(IsolationLevel.Full);
        manifest.Workspace.Network!.Subnet.ShouldBe("10.42.0.0/16");
        manifest.Workspace.Secrets!.Provider.ShouldBe("vault");
    }

    [Fact]
    public void Parse_Agents_ParsesCorrectly()
    {
        var manifest = _parser.Parse(FullManifest);

        manifest.Agents.ShouldContainKey("researcher");
        var agent = manifest.Agents["researcher"];
        agent.Model.ShouldBe("claude-sonnet-4-20250514");
        agent.MaxConcurrentTasks.ShouldBe(5);
        agent.Tools.ShouldContain("web-search");
        agent.Heartbeat!.Cron.ShouldBe("*/30 * * * *");
        agent.Heartbeat.Tasks.ShouldContain("Check for updates");
    }

    [Fact]
    public void Parse_Tools_ParsesAllTypes()
    {
        var manifest = _parser.Parse(FullManifest);

        manifest.Tools.Count.ShouldBe(2);

        var mcpTool = manifest.Tools["web-search"];
        mcpTool.Type.ShouldBe("mcp");
        mcpTool.Mcp!.Server.ShouldBe("npx");
        mcpTool.Mcp.Args.ShouldContain("-y");

        var cliTool = manifest.Tools["terminal"];
        cliTool.Type.ShouldBe("cli");
        cliTool.Cli!.Shell.ShouldBe("/bin/bash");
        cliTool.Cli.AllowedCommands.ShouldContain("git *");
        cliTool.Cli.DeniedCommands.ShouldContain("rm -rf /");
    }

    [Fact]
    public void Parse_Targets_ParsesCorrectly()
    {
        var manifest = _parser.Parse(FullManifest);

        manifest.Targets.ShouldContainKey("local");
        manifest.Targets["local"].Runtime.ShouldBe("podman");
        manifest.Targets["staging"].Replicas.ShouldBe(2);
    }

    [Fact]
    public void Validate_ValidManifest_ReturnsNoErrors()
    {
        var manifest = _parser.Parse(FullManifest);
        var errors = _parser.Validate(manifest);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_MissingVersion_ReturnsError()
    {
        var manifest = new WorkspaceManifest { Version = "", Name = "test" };
        var errors = _parser.Validate(manifest);
        errors.ShouldContain(e => e.Contains("version"));
    }

    [Fact]
    public void Validate_AgentReferencesUndefinedTool_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["agent1"] = new AgentDefinition
                {
                    Model = "test-model",
                    Tools = ["nonexistent-tool"]
                }
            }
        };

        var errors = _parser.Validate(manifest);
        errors.ShouldContain(e => e.Contains("nonexistent-tool"));
    }

    [Fact]
    public void Validate_InvalidToolType_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["bad-tool"] = new ToolDefinition { Type = "invalid" }
            }
        };

        var errors = _parser.Validate(manifest);
        errors.ShouldContain(e => e.Contains("invalid"));
    }

    [Fact]
    public void Parse_MinimalManifest_UsesDefaults()
    {
        const string yaml = """
            version: "1.0"
            name: minimal
            """;

        var manifest = _parser.Parse(yaml);

        manifest.Name.ShouldBe("minimal");
        manifest.Agents.ShouldBeEmpty();
        manifest.Tools.ShouldBeEmpty();
        manifest.Targets.ShouldBeEmpty();
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var manifest = _parser.Parse(FullManifest);
        var yaml = _parser.Serialize(manifest);
        var roundTripped = _parser.Parse(yaml);

        roundTripped.Name.ShouldBe(manifest.Name);
        roundTripped.Version.ShouldBe(manifest.Version);
        roundTripped.Agents.Count.ShouldBe(manifest.Agents.Count);
        roundTripped.Tools.Count.ShouldBe(manifest.Tools.Count);
        roundTripped.Targets.Count.ShouldBe(manifest.Targets.Count);

        var agent = roundTripped.Agents["researcher"];
        agent.Model.ShouldBe("claude-sonnet-4-20250514");
        agent.MaxConcurrentTasks.ShouldBe(5);
        agent.Tools.ShouldContain("web-search");
        agent.Heartbeat!.Cron.ShouldBe("*/30 * * * *");

        var mcpTool = roundTripped.Tools["web-search"];
        mcpTool.Type.ShouldBe("mcp");
        mcpTool.Mcp!.Server.ShouldBe("npx");

        roundTripped.Workspace.Secrets!.Provider.ShouldBe("vault");
        roundTripped.Workspace.Secrets.Vault!.Address.ShouldBe("https://vault.example.com");
    }

    [Fact]
    public void Parse_EmptyYaml_ThrowsArgumentException()
    {
        var act = () => _parser.Parse("");
        Should.Throw<ArgumentException>(act);
    }
}
