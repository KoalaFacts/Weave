using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Tests;

public sealed class ManifestParserTests
{
    private readonly ManifestParser _parser = new();

    private const string FullManifest = """
        {
          // Full workspace manifest for testing
          "version": "1.0",
          "name": "test-workspace",
          "workspace": {
            "isolation": "full",
            "network": {
              "name": "weave-test",
              "subnet": "10.42.0.0/16"
            },
            "filesystem": {
              "root": "/var/weave/workspaces/test",
              "mounts": [
                {
                  "source": "./data",
                  "target": "/workspace/data",
                  "readonly": false
                }
              ]
            },
            "secrets": {
              "provider": "vault",
              "vault": {
                "address": "https://vault.example.com",
                "mount": "weave/test"
              }
            }
          },
          "agents": {
            "researcher": {
              "model": "claude-sonnet-4-20250514",
              "system_prompt_file": "./prompts/researcher.md",
              "max_concurrent_tasks": 5,
              "memory": {
                "provider": "redis",
                "ttl": "24h"
              },
              "tools": ["web-search"],
              "capabilities": ["net:outbound"],
              "heartbeat": {
                "cron": "*/30 * * * *",
                "tasks": ["Check for updates"]
              }
            }
          },
          "tools": {
            "web-search": {
              "type": "mcp",
              "mcp": {
                "server": "npx",
                "args": ["-y", "@anthropic/mcp-server-web-search"],
                "env": {
                  "ANTHROPIC_API_KEY": "${secrets.anthropic_api_key}"
                }
              }
            },
            "terminal": {
              "type": "cli",
              "cli": {
                "shell": "/bin/bash",
                "allowed_commands": ["git *"],
                "denied_commands": ["rm -rf /"]
              }
            }
          },
          "targets": {
            "local": { "runtime": "podman" },
            "staging": { "runtime": "k3s", "replicas": 2 }
          }
        }
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
        const string json = """
            { "version": "1.0", "name": "minimal" }
            """;

        var manifest = _parser.Parse(json);

        manifest.Name.ShouldBe("minimal");
        manifest.Agents.ShouldBeEmpty();
        manifest.Tools.ShouldBeEmpty();
        manifest.Targets.ShouldBeEmpty();
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var manifest = _parser.Parse(FullManifest);
        var json = _parser.Serialize(manifest);
        var roundTripped = _parser.Parse(json);

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
    public void Parse_EmptyJson_ThrowsArgumentException()
    {
        var act = () => _parser.Parse("");
        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void Parse_JsonWithComments_ParsesCorrectly()
    {
        const string jsonc = """
            {
              // This is a comment
              "version": "1.0",
              "name": "commented",
              /* Block comment */
              "agents": {},
              "tools": {},
            }
            """;

        var manifest = _parser.Parse(jsonc);
        manifest.Name.ShouldBe("commented");
    }

    [Fact]
    public void Parse_ManifestWithPlugins_ParsesCorrectly()
    {
        const string json = """
            {
              "version": "1.0",
              "name": "with-plugins",
              "plugins": {
                "dapr-sidecar": {
                  "type": "dapr",
                  "description": "Local Dapr sidecar",
                  "config": { "port": "3500" }
                },
                "vault": {
                  "type": "vault",
                  "description": "HashiCorp Vault",
                  "config": { "address": "http://localhost:8200", "token": "root" },
                  "enabled_when": "env:VAULT_ADDR"
                }
              }
            }
            """;

        var manifest = _parser.Parse(json);

        manifest.Plugins.Count.ShouldBe(2);

        var dapr = manifest.Plugins["dapr-sidecar"];
        dapr.Type.ShouldBe("dapr");
        dapr.Description.ShouldBe("Local Dapr sidecar");
        dapr.Config["port"].ShouldBe("3500");

        var vault = manifest.Plugins["vault"];
        vault.Type.ShouldBe("vault");
        vault.Config["address"].ShouldBe("http://localhost:8200");
        vault.Config["token"].ShouldBe("root");
        vault.EnabledWhen.ShouldBe("env:VAULT_ADDR");
    }

    [Fact]
    public void Validate_InvalidPluginType_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["bad-plugin"] = new PluginDefinition { Type = "unknown" }
            }
        };

        var errors = _parser.Validate(manifest);
        errors.ShouldContain(e => e.Contains("unknown"));
    }

    [Fact]
    public void Validate_DirectHttpToolType_IsValid()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["my-api"] = new ToolDefinition
                {
                    Type = "direct_http",
                    DirectHttp = new DirectHttpConfig { BaseUrl = "http://localhost:8080" }
                }
            }
        };

        var errors = _parser.Validate(manifest);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WebhookPluginType_IsValid()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["events"] = new PluginDefinition
                {
                    Type = "webhook",
                    Config = new Dictionary<string, string> { ["url"] = "http://localhost:9000/hooks" }
                }
            }
        };

        var errors = _parser.Validate(manifest);
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_DirectHttpTool_ParsesConfig()
    {
        const string json = """
            {
              "version": "1.0",
              "name": "direct-http-test",
              "tools": {
                "my-service": {
                  "type": "direct_http",
                  "direct_http": {
                    "base_url": "http://my-service:8080",
                    "auth": { "type": "bearer", "token": "abc123" }
                  }
                }
              }
            }
            """;

        var manifest = _parser.Parse(json);

        manifest.Tools.ShouldContainKey("my-service");
        var tool = manifest.Tools["my-service"];
        tool.Type.ShouldBe("direct_http");
        tool.DirectHttp.ShouldNotBeNull();
        tool.DirectHttp!.BaseUrl.ShouldBe("http://my-service:8080");
        tool.DirectHttp.Auth.ShouldNotBeNull();
        tool.DirectHttp.Auth!.Type.ShouldBe("bearer");
        tool.DirectHttp.Auth.Token.ShouldBe("abc123");
    }

    [Fact]
    public void Serialize_PluginsRoundTrip()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "plugin-test",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["my-http"] = new PluginDefinition
                {
                    Type = "http",
                    Description = "Custom API",
                    Config = new Dictionary<string, string> { ["base_url"] = "http://api.local:3000" }
                }
            }
        };

        var json = _parser.Serialize(manifest);
        var roundTripped = _parser.Parse(json);

        roundTripped.Plugins.Count.ShouldBe(1);
        var plugin = roundTripped.Plugins["my-http"];
        plugin.Type.ShouldBe("http");
        plugin.Description.ShouldBe("Custom API");
        plugin.Config["base_url"].ShouldBe("http://api.local:3000");
    }

    // --- Parse error paths ---

    [Fact]
    public void Parse_NullInput_Throws()
    {
        Should.Throw<ArgumentException>(() => _parser.Parse(null!));
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Should.Throw<ArgumentException>(() => _parser.Parse(""));
    }

    [Fact]
    public void Parse_WhitespaceOnly_Throws()
    {
        Should.Throw<ArgumentException>(() => _parser.Parse("   "));
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsJsonException()
    {
        Should.Throw<System.Text.Json.JsonException>(() => _parser.Parse("not json"));
    }

    // --- ParseFile error paths ---

    [Fact]
    public void ParseFile_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => _parser.ParseFile(null!));
    }

    [Fact]
    public void ParseFile_EmptyPath_Throws()
    {
        Should.Throw<ArgumentException>(() => _parser.ParseFile(""));
    }

    [Fact]
    public void ParseFile_NonexistentFile_ThrowsIOException()
    {
        Should.Throw<IOException>(() => _parser.ParseFile("/nonexistent/path/manifest.json"));
    }

    // --- Serialize error paths ---

    [Fact]
    public void Serialize_Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() => _parser.Serialize(null!));
    }

    // --- Validate: agent model missing ---

    [Fact]
    public void Validate_AgentMissingModel_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["bot"] = new AgentDefinition { Model = "" }
            }
        };

        var errors = _parser.Validate(manifest);

        errors.ShouldContain(e => e.Contains("bot") && e.Contains("model"));
    }

    // --- Validate: heartbeat without cron ---

    [Fact]
    public void Validate_HeartbeatWithoutCron_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["bot"] = new AgentDefinition
                {
                    Model = "gpt-4",
                    Heartbeat = new HeartbeatConfig { Cron = "" }
                }
            }
        };

        var errors = _parser.Validate(manifest);

        errors.ShouldContain(e => e.Contains("bot") && e.Contains("cron"));
    }

    // --- Validate: target missing runtime ---

    [Fact]
    public void Validate_TargetMissingRuntime_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Targets = new Dictionary<string, TargetDefinition>
            {
                ["prod"] = new TargetDefinition { Runtime = "" }
            }
        };

        var errors = _parser.Validate(manifest);

        errors.ShouldContain(e => e.Contains("prod") && e.Contains("runtime"));
    }

    // --- Validate: unsupported version ---

    [Fact]
    public void Validate_UnsupportedVersion_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "2.0",
            Name = "test"
        };

        var errors = _parser.Validate(manifest);

        errors.ShouldContain(e => e.Contains("2.0") && e.Contains("1.0"));
    }

    // --- Validate: missing name ---

    [Fact]
    public void Validate_MissingName_ReturnsError()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = ""
        };

        var errors = _parser.Validate(manifest);

        errors.ShouldContain(e => e.Contains("name"));
    }

    // --- Validate: clean manifest ---

    [Fact]
    public void Validate_CleanManifest_ReturnsNoErrors()
    {
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = "test",
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["bot"] = new AgentDefinition { Model = "gpt-4", Tools = ["search"] }
            },
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["search"] = new ToolDefinition { Type = "cli" }
            }
        };

        _parser.Validate(manifest).ShouldBeEmpty();
    }

    // --- Parse: null collections default to empty ---

    [Fact]
    public void Parse_MinimalManifest_HasEmptyCollections()
    {
        var json = """{"version":"1.0","name":"test"}""";

        var manifest = _parser.Parse(json);

        manifest.Agents.ShouldNotBeNull();
        manifest.Agents.ShouldBeEmpty();
        manifest.Tools.ShouldNotBeNull();
        manifest.Tools.ShouldBeEmpty();
        manifest.Targets.ShouldNotBeNull();
        manifest.Plugins.ShouldNotBeNull();
    }
}
