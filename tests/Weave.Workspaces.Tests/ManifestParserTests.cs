using FluentAssertions;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;
using Xunit;

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

        manifest.Version.Should().Be("1.0");
        manifest.Name.Should().Be("test-workspace");
        manifest.Workspace.Isolation.Should().Be(IsolationLevel.Full);
        manifest.Workspace.Network!.Subnet.Should().Be("10.42.0.0/16");
        manifest.Workspace.Secrets!.Provider.Should().Be("vault");
    }

    [Fact]
    public void Parse_Agents_ParsesCorrectly()
    {
        var manifest = _parser.Parse(FullManifest);

        manifest.Agents.Should().ContainKey("researcher");
        var agent = manifest.Agents["researcher"];
        agent.Model.Should().Be("claude-sonnet-4-20250514");
        agent.MaxConcurrentTasks.Should().Be(5);
        agent.Tools.Should().Contain("web-search");
        agent.Heartbeat!.Cron.Should().Be("*/30 * * * *");
        agent.Heartbeat.Tasks.Should().Contain("Check for updates");
    }

    [Fact]
    public void Parse_Tools_ParsesAllTypes()
    {
        var manifest = _parser.Parse(FullManifest);

        manifest.Tools.Should().HaveCount(2);

        var mcpTool = manifest.Tools["web-search"];
        mcpTool.Type.Should().Be("mcp");
        mcpTool.Mcp!.Server.Should().Be("npx");
        mcpTool.Mcp.Args.Should().Contain("-y");

        var cliTool = manifest.Tools["terminal"];
        cliTool.Type.Should().Be("cli");
        cliTool.Cli!.Shell.Should().Be("/bin/bash");
        cliTool.Cli.AllowedCommands.Should().Contain("git *");
        cliTool.Cli.DeniedCommands.Should().Contain("rm -rf /");
    }

    [Fact]
    public void Parse_Targets_ParsesCorrectly()
    {
        var manifest = _parser.Parse(FullManifest);

        manifest.Targets.Should().ContainKey("local");
        manifest.Targets["local"].Runtime.Should().Be("podman");
        manifest.Targets["staging"].Replicas.Should().Be(2);
    }

    [Fact]
    public void Validate_ValidManifest_ReturnsNoErrors()
    {
        var manifest = _parser.Parse(FullManifest);
        var errors = _parser.Validate(manifest);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingVersion_ReturnsError()
    {
        var manifest = new WorkspaceManifest { Version = "", Name = "test" };
        var errors = _parser.Validate(manifest);
        errors.Should().Contain(e => e.Contains("version"));
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
        errors.Should().Contain(e => e.Contains("nonexistent-tool"));
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
        errors.Should().Contain(e => e.Contains("invalid"));
    }

    [Fact]
    public void Parse_MinimalManifest_UsesDefaults()
    {
        const string yaml = """
            version: "1.0"
            name: minimal
            """;

        var manifest = _parser.Parse(yaml);

        manifest.Name.Should().Be("minimal");
        manifest.Agents.Should().BeEmpty();
        manifest.Tools.Should().BeEmpty();
        manifest.Targets.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var manifest = _parser.Parse(FullManifest);
        var yaml = _parser.Serialize(manifest);

        yaml.Should().Contain("test-workspace");
    }

    [Fact]
    public void Parse_EmptyYaml_ThrowsArgumentException()
    {
        var act = () => _parser.Parse("");
        act.Should().Throw<ArgumentException>();
    }
}
