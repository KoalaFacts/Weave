using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

public sealed partial class McpToolConnector : IToolConnector
{
    private readonly Func<McpConfig, CancellationToken, Task<IMcpTransport>> _transportFactory;
    private readonly ILogger<McpToolConnector> _logger;
    private readonly ConcurrentDictionary<string, McpConnection> _connections = new(StringComparer.Ordinal);

    public McpToolConnector(ILogger<McpToolConnector> logger)
        : this(StdioMcpTransport.ConnectAsync, logger) { }

    internal McpToolConnector(
        Func<McpConfig, CancellationToken, Task<IMcpTransport>> transportFactory,
        ILogger<McpToolConnector> logger)
    {
        _transportFactory = transportFactory;
        _logger = logger;
    }

    public ToolType ToolType => ToolType.Mcp;

    public async Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var mcp = tool.Mcp ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no MCP configuration");

        var transport = await _transportFactory(mcp, ct);
        var connection = new McpConnection(transport, tool.Name, _logger);

        try
        {
            await connection.InitializeAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        var connectionId = Guid.NewGuid().ToString("N");
        _connections[connectionId] = connection;

        LogMcpToolConnected(tool.Name);

        return new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.Mcp,
            ConnectionId = connectionId,
            IsConnected = true
        };
    }

    public async Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        if (_connections.TryRemove(handle.ConnectionId, out var connection))
        {
            await connection.DisposeAsync();
            LogMcpToolDisconnected(handle.ToolName);
        }
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(handle.ConnectionId, out var connection))
        {
            return new ToolResult { Success = false, ToolName = handle.ToolName, Error = "MCP process not connected" };
        }

        if (connection.HasExited)
        {
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = $"MCP process exited. {connection.DiagnosticTail()}"
            };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var arguments = BuildArguments(invocation);
            var result = await connection.CallToolAsync(invocation.Method, arguments, ct);
            sw.Stop();

            var output = JoinTextContent(result.Content);

            return new ToolResult
            {
                Success = !result.IsError,
                ToolName = handle.ToolName,
                Output = output,
                Error = result.IsError ? (output.Length == 0 ? "MCP tool reported error" : output) : null,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or ObjectDisposedException or TaskCanceledException)
        {
            sw.Stop();
            LogMcpToolInvocationFailed(ex, handle.ToolName);
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    public async Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(handle.ConnectionId, out var connection))
        {
            return new ToolSchema { ToolName = handle.ToolName, Description = "MCP tool not connected" };
        }

        try
        {
            var tools = await connection.ListToolsAsync(ct);
            return new ToolSchema
            {
                ToolName = handle.ToolName,
                Description = FormatMenu(handle.ToolName, tools),
                Parameters =
                [
                    new ToolParameter
                    {
                        Name = "method",
                        Type = "string",
                        Description = $"Name of the MCP tool to invoke. Available: {string.Join(", ", tools.Select(t => t.Name))}",
                        Required = true
                    }
                ]
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or ObjectDisposedException or TaskCanceledException)
        {
            LogMcpToolSchemaDiscoveryFailed(ex, handle.ToolName);
            return new ToolSchema { ToolName = handle.ToolName, Description = $"MCP tool: {handle.ToolName} (schema discovery failed: {ex.Message})" };
        }
    }

    private static JsonObject BuildArguments(ToolInvocation invocation)
    {
        // The agent's ToolInvocationBuilder stringifies non-string values as raw JSON;
        // round-trip those back into nodes so the MCP server sees proper types.
        var args = new JsonObject();
        foreach (var (key, value) in invocation.Parameters)
        {
            args[key] = TryParseJsonNode(value);
        }
        return args;
    }

    private static JsonNode TryParseJsonNode(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return JsonValue.Create(raw);

        var trimmed = raw.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[' && trimmed[0] != '"'
            && !char.IsDigit(trimmed[0]) && trimmed[0] != '-' && trimmed[0] != 't' && trimmed[0] != 'f' && trimmed[0] != 'n'))
        {
            return JsonValue.Create(raw);
        }

        try { return JsonNode.Parse(raw) ?? JsonValue.Create(raw); }
        catch (JsonException) { return JsonValue.Create(raw); }
    }

    private static string JoinTextContent(IReadOnlyList<McpContentBlock> blocks)
    {
        if (blocks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block.Type == "text" && block.Text is not null)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(block.Text);
            }
        }
        return sb.ToString();
    }

    private static string FormatMenu(string handleName, IReadOnlyList<McpTool> tools)
    {
        if (tools.Count == 0)
            return $"MCP tool '{handleName}' exposes no tools.";

        var sb = new StringBuilder();
        sb.Append("MCP tool '").Append(handleName).Append("' exposes: ");
        for (var i = 0; i < tools.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append(tools[i].Name);
            if (!string.IsNullOrEmpty(tools[i].Description))
                sb.Append(" — ").Append(tools[i].Description);
        }
        sb.Append('.');
        return sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool '{Tool}' connected")]
    private partial void LogMcpToolConnected(string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool '{Tool}' disconnected")]
    private partial void LogMcpToolDisconnected(string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool '{Tool}' invocation failed")]
    private partial void LogMcpToolInvocationFailed(Exception ex, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool '{Tool}' schema discovery failed")]
    private partial void LogMcpToolSchemaDiscoveryFailed(Exception ex, string tool);
}
