using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed partial class McpToolConnector(ILogger<McpToolConnector> logger) : IToolConnector
{
    private const int MaxStderrTailLines = 50;
    private readonly Dictionary<string, McpConnection> _processes = [];

    public ToolType ToolType => ToolType.Mcp;

    public async Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var mcp = tool.Mcp ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no MCP configuration");

        var psi = new ProcessStartInfo
        {
            FileName = mcp.Server,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in mcp.Args)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in mcp.Env)
            psi.Environment[key] = value;

        var process = new Process { StartInfo = psi };

        // Ring buffer of recent stderr lines so a dead-process Invoke
        // can attach diagnostic context. The pipe drainer below runs
        // on a pool thread; without it, verbose MCP servers hang once
        // the ~4 KB stderr buffer fills (see docs/best-practices.md —
        // "If RedirectStandardError = true, drain stderr too").
        var stderrTail = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stderrTail.Enqueue(e.Data);
            while (stderrTail.Count > MaxStderrTailLines)
                stderrTail.TryDequeue(out string? _);
        };

        process.Start();
        process.BeginErrorReadLine();

        var connectionId = Guid.NewGuid().ToString("N");
        _processes[connectionId] = new McpConnection(process, stderrTail);

        LogMcpToolConnected(tool.Name, process.Id);

        await Task.CompletedTask;
        return new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.Mcp,
            ConnectionId = connectionId,
            IsConnected = true
        };
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        if (_processes.Remove(handle.ConnectionId, out var connection))
        {
            if (!connection.Process.HasExited)
                connection.Process.Kill(entireProcessTree: true);
            connection.Process.Dispose();
            LogMcpToolDisconnected(handle.ToolName);
        }
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_processes.TryGetValue(handle.ConnectionId, out var connection) || connection.Process.HasExited)
        {
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = connection is null
                    ? "MCP process not connected"
                    : $"MCP process exited (code {connection.Process.ExitCode}). {FormatStderrTail(connection.StderrTail)}"
            };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var request = JsonSerializer.Serialize(new JsonRpcRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Method = invocation.Method,
                Params = invocation.Parameters
            }, McpJsonContext.Default.JsonRpcRequest);

            await connection.Process.StandardInput.WriteLineAsync(request.AsMemory(), ct);
            var response = await connection.Process.StandardOutput.ReadLineAsync(ct);
            sw.Stop();

            // Null response means the child closed stdout — usually a
            // crash. Surface stderr tail instead of pretending the call
            // succeeded with empty output.
            if (response is null)
            {
                return new ToolResult
                {
                    Success = false,
                    ToolName = handle.ToolName,
                    Error = connection.Process.HasExited
                        ? $"MCP process exited (code {connection.Process.ExitCode}) during invoke. {FormatStderrTail(connection.StderrTail)}"
                        : $"MCP process closed stdout without a response. {FormatStderrTail(connection.StderrTail)}",
                    Duration = sw.Elapsed
                };
            }

            return new ToolResult
            {
                Success = true,
                ToolName = handle.ToolName,
                Output = response,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = $"{ex.Message} {FormatStderrTail(connection.StderrTail)}",
                Duration = sw.Elapsed
            };
        }
    }

    internal static string FormatStderrTail(ConcurrentQueue<string> stderrTail)
    {
        if (stderrTail.IsEmpty)
            return string.Empty;
        var lines = stderrTail.ToArray();
        return $"stderr tail: {string.Join(" | ", lines[^Math.Min(5, lines.Length)..])}";
    }

    private sealed record McpConnection(Process Process, ConcurrentQueue<string> StderrTail);

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = $"MCP tool: {handle.ToolName}"
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool '{Tool}' connected (pid: {Pid})")]
    private partial void LogMcpToolConnected(string tool, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool '{Tool}' disconnected")]
    private partial void LogMcpToolDisconnected(string tool);
}

