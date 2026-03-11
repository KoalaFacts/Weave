using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed class McpToolConnector(ILogger<McpToolConnector> logger) : IToolConnector
{
    private readonly Dictionary<string, Process> _processes = [];

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
        process.Start();

        var connectionId = Guid.NewGuid().ToString("N");
        _processes[connectionId] = process;

        logger.LogInformation("MCP tool '{Tool}' connected (pid: {Pid})", tool.Name, process.Id);

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
        if (_processes.Remove(handle.ConnectionId, out var process))
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
            logger.LogInformation("MCP tool '{Tool}' disconnected", handle.ToolName);
        }
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_processes.TryGetValue(handle.ConnectionId, out var process) || process.HasExited)
        {
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = "MCP process not connected"
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

            await process.StandardInput.WriteLineAsync(request.AsMemory(), ct);
            var response = await process.StandardOutput.ReadLineAsync(ct);
            sw.Stop();

            return new ToolResult
            {
                Success = true,
                ToolName = handle.ToolName,
                Output = response ?? string.Empty,
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
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = $"MCP tool: {handle.ToolName}"
        });
    }
}

internal sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public Dictionary<string, string>? Params { get; init; }
}

[JsonSerializable(typeof(JsonRpcRequest))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
