using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Weave.Tools.Connectors;

internal sealed partial class McpConnection : IAsyncDisposable
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ClientName = "weave";
    private const string ClientVersion = "0.1.0";

    private readonly IMcpTransport _transport;
    private readonly ILogger _logger;
    private readonly string _toolName;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _toolsLock = new(1, 1);

    private Task? _readLoop;
    private long _nextId;
    private IReadOnlyList<McpTool>? _toolsCache;

    public McpConnection(IMcpTransport transport, string toolName, ILogger logger)
    {
        _transport = transport;
        _toolName = toolName;
        _logger = logger;
    }

    public bool HasExited => _transport.HasExited;

    public string DiagnosticTail() => _transport.FormatDiagnosticTail();

    public async Task InitializeAsync(CancellationToken ct)
    {
        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);

        var initParams = new McpInitializeParams
        {
            ProtocolVersion = ProtocolVersion,
            Capabilities = new McpClientCapabilities(),
            ClientInfo = new McpClientInfo { Name = ClientName, Version = ClientVersion }
        };

        var paramsNode = JsonSerializer.SerializeToNode(initParams, McpJsonContext.Default.McpInitializeParams);
        var result = await SendRequestAsync("initialize", paramsNode, ct);
        var initResult = result.Deserialize(McpJsonContext.Default.McpInitializeResult);
        LogMcpInitialized(_toolName, initResult?.ServerInfo?.Name ?? "?", initResult?.ProtocolVersion ?? "?");

        await SendNotificationAsync("notifications/initialized", paramsNode: null, ct);
    }

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct)
    {
        if (_toolsCache is not null)
            return _toolsCache;

        await _toolsLock.WaitAsync(ct);
        try
        {
            if (_toolsCache is not null)
                return _toolsCache;

            var result = await SendRequestAsync("tools/list", paramsNode: null, ct);
            var listing = result.Deserialize(McpJsonContext.Default.McpToolListResult);
            _toolsCache = listing?.Tools ?? [];
            return _toolsCache;
        }
        finally
        {
            _toolsLock.Release();
        }
    }

    public async Task<McpToolCallResult> CallToolAsync(string name, JsonNode arguments, CancellationToken ct)
    {
        var callParams = new McpToolCallParams { Name = name, Arguments = arguments };
        var paramsNode = JsonSerializer.SerializeToNode(callParams, McpJsonContext.Default.McpToolCallParams)
            ?? throw new InvalidOperationException("Failed to serialize tools/call params");
        var result = await SendRequestAsync("tools/call", paramsNode, ct);
        return result.Deserialize(McpJsonContext.Default.McpToolCallResult)
            ?? new McpToolCallResult { IsError = true };
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode? paramsNode, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            var request = new McpJsonRpcRequest { Id = id, Method = method, Params = paramsNode };
            var json = JsonSerializer.Serialize(request, McpJsonContext.Default.McpJsonRpcRequest);

            await _writeLock.WaitAsync(ct);
            try
            { await _transport.SendAsync(json, ct); }
            finally { _writeLock.Release(); }

            using var registration = ct.Register(static state =>
            {
                var (tcsRef, ctRef) = ((TaskCompletionSource<JsonElement>, CancellationToken))state!;
                tcsRef.TrySetCanceled(ctRef);
            }, (tcs, ct));

            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendNotificationAsync(string method, JsonNode? paramsNode, CancellationToken ct)
    {
        var notification = new McpJsonRpcNotification { Method = method, Params = paramsNode };
        var json = JsonSerializer.Serialize(notification, McpJsonContext.Default.McpJsonRpcNotification);

        await _writeLock.WaitAsync(ct);
        try
        { await _transport.SendAsync(json, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                { line = await _transport.ReceiveAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    FailAllPending(new IOException($"MCP transport read failed: {ex.Message}. {_transport.FormatDiagnosticTail()}", ex));
                    return;
                }

                if (line is null)
                {
                    FailAllPending(new IOException(
                        _transport.HasExited
                            ? $"MCP process exited (code {_transport.ExitCode}). {_transport.FormatDiagnosticTail()}"
                            : $"MCP transport closed. {_transport.FormatDiagnosticTail()}"));
                    return;
                }

                Dispatch(line);
            }
        }
        finally
        {
            FailAllPending(new TaskCanceledException("MCP connection closed"));
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument doc;
        try
        { doc = JsonDocument.Parse(line); }
        catch (JsonException ex)
        {
            LogMcpDispatchParseFailed(ex, _toolName, Truncate(line));
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("id", out var idElement) || idElement.ValueKind == JsonValueKind.Null)
            {
                LogMcpServerNotification(_toolName, doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "?" : "?");
                return;
            }

            if (!idElement.TryGetInt64(out var id))
                return;

            if (!_pending.TryRemove(id, out var tcs))
                return;

            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
                var message = error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "(no message)" : "(no message)";
                tcs.TrySetException(new InvalidOperationException($"MCP error {code}: {message}"));
                return;
            }

            if (doc.RootElement.TryGetProperty("result", out var result))
            {
                tcs.TrySetResult(result.Clone());
                return;
            }

            tcs.TrySetException(new InvalidOperationException("MCP response missing both 'result' and 'error'"));
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
                tcs.TrySetException(ex);
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "...";

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_readLoop is not null)
            await _readLoop;
        await _transport.DisposeAsync();
        _shutdown.Dispose();
        _writeLock.Dispose();
        _toolsLock.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP '{Tool}' initialized (server: {ServerName}, protocol: {Protocol})")]
    private partial void LogMcpInitialized(string tool, string serverName, string protocol);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MCP '{Tool}' received server notification '{Method}'")]
    private partial void LogMcpServerNotification(string tool, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP '{Tool}' could not parse incoming frame: {Frame}")]
    private partial void LogMcpDispatchParseFailed(Exception ex, string tool, string frame);
}
