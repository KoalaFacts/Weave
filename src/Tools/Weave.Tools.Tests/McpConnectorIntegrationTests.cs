using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Tests;

// Progression evidence: the MCP connector launches a real subprocess and speaks
// real JSON-RPC 2.0 over actual stdio pipes — no Channel<string> stub, no
// in-process transport. Verifies the protocol layer works against a real reader
// on the other side.
public sealed class McpConnectorIntegrationTests : IDisposable
{
    private static readonly CapabilityToken _token = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:mcp-integration"]
    };

    private const string PythonServer = """
        import json, sys

        def send(obj):
            sys.stdout.write(json.dumps(obj) + "\n")
            sys.stdout.flush()

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            req = json.loads(line)
            method = req.get("method")
            rid = req.get("id")
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": rid, "result": {
                    "protocolVersion": "2024-11-05",
                    "serverInfo": {"name": "py-mcp-stub", "version": "0.1.0"}
                }})
            elif method == "notifications/initialized":
                pass
            elif method == "tools/list":
                send({"jsonrpc": "2.0", "id": rid, "result": {"tools": [
                    {"name": "echo", "description": "Echo the input text"},
                    {"name": "add", "description": "Add two numbers"}
                ]}})
            elif method == "tools/call":
                params_ = req.get("params", {})
                name = params_.get("name")
                args = params_.get("arguments", {})
                if name == "echo":
                    text = args.get("text", "")
                    send({"jsonrpc": "2.0", "id": rid, "result": {
                        "content": [{"type": "text", "text": f"echo:{text}"}],
                        "isError": False
                    }})
                elif name == "add":
                    try:
                        total = int(args.get("a", 0)) + int(args.get("b", 0))
                        send({"jsonrpc": "2.0", "id": rid, "result": {
                            "content": [{"type": "text", "text": str(total)}],
                            "isError": False
                        }})
                    except ValueError as e:
                        send({"jsonrpc": "2.0", "id": rid, "result": {
                            "content": [{"type": "text", "text": str(e)}],
                            "isError": True
                        }})
                else:
                    send({"jsonrpc": "2.0", "id": rid, "error": {
                        "code": -32601, "message": f"unknown tool: {name}"
                    }})
            else:
                send({"jsonrpc": "2.0", "id": rid, "error": {
                    "code": -32601, "message": f"unknown method: {method}"
                }})
        """;

    private readonly string _scriptPath;

    public McpConnectorIntegrationTests()
    {
        _scriptPath = Path.Join(Path.GetTempPath(), $"weave-mcp-stub-{Guid.NewGuid():N}.py");
        File.WriteAllText(_scriptPath, PythonServer);
    }

    public void Dispose()
    {
        try { File.Delete(_scriptPath); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task RealSubprocess_HandshakeListAndCall_WorksEndToEnd()
    {
        var python = LocatePython();
        Assert.SkipWhen(python is null, "python3 required for MCP subprocess integration test");

        var connector = new McpToolConnector(NullLogger<McpToolConnector>.Instance);
        var spec = new ToolSpec
        {
            Name = "py-stub",
            Type = ToolType.Mcp,
            Mcp = new McpConfig { Server = python!, Args = [_scriptPath] }
        };

        var handle = await connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken);
        handle.IsConnected.ShouldBeTrue();

        try
        {
            var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);
            schema.Description.ShouldContain("echo");
            schema.Description.ShouldContain("add");

            var echoResult = await connector.InvokeAsync(handle,
                new ToolInvocation { ToolName = "py-stub", Method = "echo", Parameters = new() { ["text"] = "hello-from-test" } },
                TestContext.Current.CancellationToken);
            echoResult.Success.ShouldBeTrue();
            echoResult.Output.ShouldBe("echo:hello-from-test");

            var addResult = await connector.InvokeAsync(handle,
                new ToolInvocation { ToolName = "py-stub", Method = "add", Parameters = new() { ["a"] = "17", ["b"] = "25" } },
                TestContext.Current.CancellationToken);
            addResult.Success.ShouldBeTrue();
            addResult.Output.ShouldBe("42");

            var errorResult = await connector.InvokeAsync(handle,
                new ToolInvocation { ToolName = "py-stub", Method = "unknown" },
                TestContext.Current.CancellationToken);
            errorResult.Success.ShouldBeFalse();
            errorResult.Error!.ShouldContain("unknown tool");
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
            foreach (var candidate in pathEnv.Split(Path.PathSeparator).Select(dir => Path.Join(dir, name)))
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}
