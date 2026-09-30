using Weave.Agents.ToolRegistry;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Tests;

public sealed class McpToolRecoveryPlanTests
{
    [Theory]
    [InlineData("url")]
    [InlineData("plugin")]
    [InlineData("version")]
    [InlineData("request-timeout")]
    [InlineData("idle-timeout")]
    [InlineData("response-bytes")]
    [InlineData("frame-bytes")]
    [InlineData("queued-frames")]
    public void From_ConnectionSettingChanges_ChangesFrozenDigest(string setting)
    {
        var definition = Definition();
        var before = McpToolRecoveryPlan.From("echo", definition);
        before.Supported.ShouldBeTrue();
        var config = definition.Mcp!;
        var changed = setting switch
        {
            "url" => definition with { Mcp = config with { Url = "http://127.0.0.1:5678/mcp" } },
            "plugin" => definition with { RequiresPlugin = "other" },
            "version" => definition with { Version = "2" },
            "request-timeout" => definition with { Mcp = config with { RequestTimeoutSeconds = 15 } },
            "idle-timeout" => definition with { Mcp = config with { IdleTimeoutSeconds = 15 } },
            "response-bytes" => definition with { Mcp = config with { MaxResponseBytes = 4096 } },
            "frame-bytes" => definition with { Mcp = config with { MaxFrameBytes = 4096 } },
            "queued-frames" => definition with { Mcp = config with { MaxQueuedFrames = 16 } },
            _ => throw new ArgumentOutOfRangeException(nameof(setting))
        };
        var after = McpToolRecoveryPlan.From("echo", changed);
        after.Supported.ShouldBeTrue();
        after.Digest.ShouldNotBe(before.Digest);
    }

    [Fact]
    public void From_MutableEnvironmentChanges_DoesNotChangeOwnedPreviousSnapshot()
    {
        var definition = Definition();
        var before = McpToolRecoveryPlan.From("echo", definition);
        var digest = before.Digest;
        definition.Mcp!.Env["TEST_VALUE"] = "unsupported-environment";
        McpToolRecoveryPlan.From("echo", definition).Supported.ShouldBeFalse();
        before.Supported.ShouldBeTrue();
        before.Digest.ShouldBe(digest);
    }

    private static ToolDefinition Definition() => new()
    {
        Type = "mcp",
        RequiresPlugin = "server",
        Mcp = new() { Url = "http://127.0.0.1:1234/mcp", AllowPrivateEndpoints = true }
    };
}
