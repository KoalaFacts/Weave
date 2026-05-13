using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Tests;

// Progression evidence: the repo-shipped examples/echo-mcp/server.py works
// against the production McpToolConnector with no test stubs. Locks in the
// example so it can't bit-rot — if the server or the connector drifts, this
// fails and points at the cause.
public sealed class EchoMcpServerSmokeTests
{
    private static readonly CapabilityToken _token = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:echo-server"]
    };

    [Fact]
    public async Task ExampleServer_StdioMode_RoundTripsThroughRealConnector()
    {
        var python = LocatePython();
        Assert.SkipWhen(python is null, "python3 required for echo-mcp smoke test");

        var scriptPath = LocateServerScript();
        Assert.SkipWhen(scriptPath is null, "examples/echo-mcp/server.py not found relative to repo root");

        var connector = new McpToolConnector(NullLogger<McpToolConnector>.Instance);
        var spec = new ToolSpec
        {
            Name = "echo-server",
            Type = ToolType.Mcp,
            Mcp = new McpConfig { Server = python!, Args = [scriptPath!, "--transport", "stdio"] }
        };

        var handle = await connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken);
        try
        {
            var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);
            schema.Description.ShouldContain("echo");

            var result = await connector.InvokeAsync(handle,
                new ToolInvocation
                {
                    ToolName = "echo-server",
                    Method = "echo",
                    Parameters = new() { ["text"] = "hello from weave" }
                },
                TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            result.Output.ShouldBe("hello from weave");
        }
        finally
        {
            await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
        }
    }

    private static string? LocatePython()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv is null) continue;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var candidate = Path.Join(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string? LocateServerScript()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Join(dir, "examples", "echo-mcp", "server.py");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
