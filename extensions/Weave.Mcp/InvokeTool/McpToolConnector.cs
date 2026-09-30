using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Tools.InstallMcpTool;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

public sealed partial class McpToolConnector : IToolConnector, IApprovalTargetBinding
{
    private readonly Func<McpConfig, CancellationToken, Task<IMcpTransport>> _transportFactory;
    private readonly ILogger<McpToolConnector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, McpConnection> _connections = new(StringComparer.Ordinal);
    private readonly McpInstallationContract? _installation;
    private readonly object _dispatchGate = new();
    private TaskCompletionSource? _idle;
    private bool _active = true;
    private int _inFlight;
    private string? _observedContractDigest;

    public McpToolConnector(ILogger<McpToolConnector> logger, TimeProvider? timeProvider = null)
        : this(SelectTransport, logger, timeProvider) { }

    public McpToolConnector(McpInstallationContract installation, ILogger<McpToolConnector> logger,
        TimeProvider? timeProvider = null)
        : this(SelectTransport, logger, timeProvider) => _installation = installation;

    private static Task<IMcpTransport> SelectTransport(McpConfig config, CancellationToken ct)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(config.Url);
        var hasServer = !string.IsNullOrWhiteSpace(config.Server);

        if (hasUrl && hasServer)
            throw new InvalidOperationException("McpConfig must set exactly one of 'server' (stdio) or 'url' (http) — not both.");
        if (!hasUrl && !hasServer)
            throw new InvalidOperationException("McpConfig must set either 'server' (stdio) or 'url' (http).");

        return hasUrl ? HttpMcpTransport.ConnectAsync(config, ct) : StdioMcpTransport.ConnectAsync(config, ct);
    }

    internal McpToolConnector(
        Func<McpConfig, CancellationToken, Task<IMcpTransport>> transportFactory,
        ILogger<McpToolConnector> logger,
        TimeProvider? timeProvider = null)
    {
        _transportFactory = transportFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ToolType ToolType => ToolType.Mcp;
    public string? ContractDigest => _installation?.ContractDigest ?? Volatile.Read(ref _observedContractDigest);
    public bool IsActive
    {
        get
        {
            lock (_dispatchGate)
                return _active;
        }
    }

    public void BeginDeactivate()
    {
        lock (_dispatchGate)
            _active = false;
    }

    public async Task DeactivateAsync()
    {
        Task wait;
        lock (_dispatchGate)
        {
            _active = false;
            wait = _inFlight == 0 ? Task.CompletedTask : (_idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await wait;
        var connections = _connections.ToArray();
        foreach (var item in connections)
            _connections.TryRemove(item.Key, out _);
        await Task.WhenAll(connections.Select(static item => item.Value.DisposeAsync().AsTask()));
    }

    public ToolInvocation NormalizeInvocation(ToolInvocation invocation) => invocation;

    public string? GetApprovalTargetDigest(ToolHandle handle) => _installation is null || ContractDigest is null
        ? null
        : McpToolInstallation.ComputeConfigDigest(_installation.Url, _installation.ServerName,
            _installation.ServerVersion, _installation.Operation) + ":" + ContractDigest;

    public string? GetApprovalTargetDescription(ToolHandle handle) => _installation is null
        ? null
        : $"MCP {_installation.ServerName} {_installation.ServerVersion} at {_installation.Url}; "
            + $"operation {_installation.Operation}; contract SHA-256 {ContractDigest}";

    public async Task ProbeAsync(McpConfig config, CancellationToken ct = default)
    {
        if (_installation is null || !string.Equals(config.Url, _installation.Url, StringComparison.Ordinal))
            throw new InvalidOperationException("MCP probe endpoint differs from its installation.");
        await using var connection = await OpenConnectionAsync(config, _installation.Operation, ct);
        await VerifyContractAsync(connection, refresh: false, ct);
    }

    public async Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var mcp = tool.Mcp ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no MCP configuration");
        if (_installation is not null && (!string.Equals(mcp.Url, _installation.Url, StringComparison.Ordinal)
            || mcp.Server is not null || mcp.Args.Count != 0 || mcp.Env.Count != 0))
            throw new UnauthorizedAccessException("MCP tool endpoint differs from its installation.");
        lock (_dispatchGate)
        {
            if (!_active)
                throw new InvalidOperationException("MCP installation is inactive.");
        }

        var connection = await OpenConnectionAsync(mcp, tool.Name, ct);

        try
        {
            if (_installation is not null)
                await VerifyContractAsync(connection, refresh: false, ct);
            var connectionId = Guid.NewGuid().ToString("N");
            lock (_dispatchGate)
            {
                if (!_active)
                    throw new InvalidOperationException("MCP installation is inactive.");
                _connections[connectionId] = connection;
            }

            LogMcpToolConnected(tool.Name);
            return new ToolHandle
            {
                ToolName = tool.Name,
                Type = ToolType.Mcp,
                ConnectionId = connectionId,
                IsConnected = true
            };
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<McpConnection> OpenConnectionAsync(McpConfig config, string toolName, CancellationToken ct)
    {
        var connection = new McpConnection(await _transportFactory(config, ct), toolName, _logger, _timeProvider);
        bool ready;
        try
        {
            ready = await connection.InitializeAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        if (ready)
            return connection;

        await connection.DisposeAsync();
        var legacy = new McpConnection(await _transportFactory(config, ct), toolName, _logger, _timeProvider);
        try
        {
            await legacy.InitializeAsync(ct, legacyOnly: true);
            return legacy;
        }
        catch
        {
            await legacy.DisposeAsync();
            throw;
        }
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
        lock (_dispatchGate)
        {
            if (!_active)
                return new ToolResult { Success = false, ToolName = handle.ToolName, Error = "MCP installation is inactive." };
            _inFlight++;
        }

        try
        {
            return await InvokeConnectedAsync(handle, invocation, ct);
        }
        finally
        {
            lock (_dispatchGate)
            {
                if (--_inFlight == 0)
                    _idle?.TrySetResult();
            }
        }
    }

    private async Task<ToolResult> InvokeConnectedAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct)
    {
        if (_installation is not null && !string.Equals(invocation.Method, _installation.Operation, StringComparison.Ordinal))
            return new ToolResult { Success = false, ToolName = handle.ToolName, Error = "MCP operation is not granted by this installation." };
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
            if (_installation is not null)
                await ProbeAsync(_installation.ProbeConfig(), ct);
            if (_installation is not null)
                await VerifyContractAsync(connection, refresh: true, ct);
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

    private async Task VerifyContractAsync(McpConnection connection, bool refresh, CancellationToken ct)
    {
        var installation = _installation!;
        if (!string.Equals(connection.ServerName, installation.ServerName, StringComparison.Ordinal)
            || !string.Equals(connection.ServerVersion, installation.ServerVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("MCP server identity differs from the installed contract.");
        var tools = await connection.ListToolsAsync(ct, refresh);
        var operation = tools.SingleOrDefault(tool => string.Equals(tool.Name, installation.Operation, StringComparison.Ordinal));
        if (operation is null)
            throw new InvalidOperationException("MCP operation is missing or ambiguous.");
        var serialized = JsonSerializer.SerializeToUtf8Bytes(operation, McpJsonContext.Default.McpTool);
        var digest = connection.ProtocolVersion switch
        {
            "2026-07-28" or "2025-11-25" => ComputeVersionedContractDigest(connection.ProtocolVersion, serialized),
            "2024-11-05" => Convert.ToHexString(SHA256.HashData(serialized)),
            _ => throw new InvalidOperationException("MCP connection has no supported protocol revision.")
        };
        if (installation.ContractDigest is not null && !string.Equals(digest, installation.ContractDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("MCP operation contract differs from the installed revision.");
        var observed = Interlocked.CompareExchange(ref _observedContractDigest, digest, null);
        if (observed is not null && !string.Equals(observed, digest, StringComparison.Ordinal))
            throw new InvalidOperationException("MCP operation contract changed during this installation.");
    }

    private static string ComputeVersionedContractDigest(string version, byte[] serialized)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"mcp_tools/2\n{version}\n"));
        hash.AppendData(serialized);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(handle.ConnectionId, out var connection))
        {
            return new ToolSchema { ToolName = handle.ToolName, Description = "MCP tool not connected" };
        }

        try
        {
            var tools = await connection.ListToolsAsync(ct, refresh: _installation is not null);
            if (_installation is not null)
            {
                await VerifyContractAsync(connection, refresh: false, ct);
                tools = tools.Where(tool => string.Equals(tool.Name, _installation.Operation, StringComparison.Ordinal)).ToList();
            }
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

        try
        { return JsonNode.Parse(raw) ?? JsonValue.Create(raw); }
        catch (JsonException) { return JsonValue.Create(raw); }
    }

    private static string JoinTextContent(IReadOnlyList<McpContentBlock> blocks)
    {
        if (blocks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in blocks.Where(b => b.Type == "text" && b.Text is not null))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(block.Text);
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
            if (i > 0)
                sb.Append("; ");
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
