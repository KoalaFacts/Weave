using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Tests;

// These tests use a real local subprocess/socket. Retain bounded diagnostics
// so a failed handshake or stream reports the actual server/transport failure.
public sealed class EchoMcpHttpTransportSmokeTests : IDisposable
{
    private static readonly CapabilityToken _token = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:echo-server-http"]
    };

    private readonly ConcurrentQueue<string> _diagnostics = new();
    private Process? _server;
    private int _port;

    public void Dispose()
    {
        if (_server is null)
            return;
        try
        {
            if (!_server.HasExited)
                _server.Kill(entireProcessTree: true);
            _server.WaitForExit(2000);
        }
        catch (InvalidOperationException) { /* already exited */ }
        _server.Dispose();
    }

    [Fact]
    public async Task ExampleServer_HttpMode_RoundTripsThroughHttpTransport()
    {
        var python = LocatePython();
        Assert.SkipWhen(python is null, "python3 required for echo-mcp HTTP smoke test");
        var scriptPath = LocateServerScript();
        Assert.SkipWhen(scriptPath is null, "examples/echo-mcp/server.py not found relative to repo root");
        _port = FindFreePort();
        _server = StartServer(python!, scriptPath!, _port);
        await WaitForListenerAsync(_port, TestContext.Current.CancellationToken);
        var connector = new McpToolConnector(new DiagnosticLogger(AddDiagnostic));
        var spec = new ToolSpec
        {
            Name = "echo-server-http",
            Type = ToolType.Mcp,
            Mcp = new McpConfig { Url = $"http://127.0.0.1:{_port}/mcp", AllowPrivateEndpoints = true }
        };
        var handle = await connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken);
        try
        {
            var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);
            schema.Description.ShouldContain("echo");
            var result = await connector.InvokeAsync(handle,
                new ToolInvocation
                {
                    ToolName = "echo-server-http",
                    Method = "echo",
                    Parameters = new() { ["text"] = "hello over http" }
                }, TestContext.Current.CancellationToken);
            result.Error.ShouldBeNull(string.Join('\n', _diagnostics));
            result.Success.ShouldBeTrue();
            result.Output.ShouldBe("hello over http");
        }
        finally
        {
            await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExampleServer_HttpMode_SseResponse_DeliversFinalResultThroughTransport()
    {
        var python = LocatePython();
        Assert.SkipWhen(python is null, "python3 required for echo-mcp SSE smoke test");
        var scriptPath = LocateServerScript();
        Assert.SkipWhen(scriptPath is null, "examples/echo-mcp/server.py not found relative to repo root");
        _port = FindFreePort();
        _server = StartServer(python!, scriptPath!, _port);
        await WaitForListenerAsync(_port, TestContext.Current.CancellationToken);
        var connector = new McpToolConnector(new DiagnosticLogger(AddDiagnostic));
        var spec = new ToolSpec
        {
            Name = "echo-server-http-sse",
            Type = ToolType.Mcp,
            Mcp = new McpConfig { Url = $"http://127.0.0.1:{_port}/mcp", AllowPrivateEndpoints = true }
        };
        var handle = await connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken);
        try
        {
            // Progress notifications precede the final matching response.
            var result = await connector.InvokeAsync(handle,
                new ToolInvocation
                {
                    ToolName = "echo-server-http-sse",
                    Method = "echo",
                    Parameters = new() { ["text"] = "stream-me", ["chunk_size"] = "2" }
                }, TestContext.Current.CancellationToken);
            result.Error.ShouldBeNull(string.Join('\n', _diagnostics));
            result.Success.ShouldBeTrue();
            result.Output.ShouldBe("stream-me");
        }
        finally
        {
            await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ConnectAsync_UnreachableHttpEndpoint_ThrowsCleanError()
    {
        var freePort = FindFreePort();
        var connector = new McpToolConnector(NullLogger<McpToolConnector>.Instance);
        var spec = new ToolSpec
        {
            Name = "unreachable",
            Type = ToolType.Mcp,
            Mcp = new McpConfig { Url = $"http://127.0.0.1:{freePort}/mcp", AllowPrivateEndpoints = true }
        };
        await Should.ThrowAsync<IOException>(
            () => connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectAsync_BothServerAndUrl_Throws()
    {
        var connector = new McpToolConnector(NullLogger<McpToolConnector>.Instance);
        var spec = new ToolSpec
        {
            Name = "x",
            Type = ToolType.Mcp,
            Mcp = new McpConfig { Server = "python3", Url = "http://example.test/mcp" }
        };
        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectAsync_NeitherServerNorUrl_Throws()
    {
        var connector = new McpToolConnector(NullLogger<McpToolConnector>.Instance);
        var spec = new ToolSpec { Name = "x", Type = ToolType.Mcp, Mcp = new McpConfig() };
        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken));
    }

    private void AddDiagnostic(string text)
    {
        _diagnostics.Enqueue(text.Length > 4000 ? text[..4000] : text);
        while (_diagnostics.Count > 32)
            _diagnostics.TryDequeue(out _);
    }

    private Process StartServer(string python, string script, int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--transport");
        psi.ArgumentList.Add("http");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) AddDiagnostic(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) AddDiagnostic(e.Data); };
        if (!proc.Start())
        {
            proc.Dispose();
            throw new InvalidOperationException("Failed to start echo-mcp HTTP server");
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static async Task WaitForListenerAsync(int port, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync("127.0.0.1", port, ct);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, ct);
            }
        }
        throw new TimeoutException($"echo-mcp http server did not bind to port {port} within 5s");
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static string? LocatePython()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv is null)
                continue;
            foreach (var candidate in pathEnv.Split(Path.PathSeparator).Select(dir => Path.Join(dir, name)))
                if (File.Exists(candidate))
                    return candidate;
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

    private sealed class DiagnosticLogger(Action<string> write) : ILogger<McpToolConnector>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                write(exception.ToString());
        }
    }
}
