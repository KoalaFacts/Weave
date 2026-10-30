using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Tool;
namespace Weave.Tools.Connectors;

/// <summary>
/// HTTP-based tool connector — calls remote services directly by URL.
/// No sidecar or service mesh required. A lightweight alternative to
/// <see cref="DaprToolConnector"/> for environments without Dapr.
/// </summary>
public sealed partial class DirectHttpToolConnector(HttpClient httpClient, ILogger<DirectHttpToolConnector> logger) : IToolConnector
{
    private readonly ConcurrentDictionary<string, (ToolHandle Handle, DirectHttpToolConfig Config)> _connections = new(StringComparer.Ordinal);

    public ToolType ToolType => ToolType.DirectHttp;

    public ToolInvocation NormalizeInvocation(ToolInvocation invocation) => invocation with { Method = invocation.Method.TrimStart('/') };

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var config = tool.DirectHttp ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no DirectHttp configuration");

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new InvalidOperationException($"Tool '{tool.Name}': DirectHttp 'base_url' is required");

        var handle = new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.DirectHttp,
            ConnectionId = $"http:{Guid.NewGuid():N}",
            IsConnected = true
        };
        // A display name or endpoint is not a connection identity. Keep the
        // immutable destination and credential together for the handle's lifetime.
        _connections[handle.ConnectionId] = (handle, config);
        LogDirectHttpToolConnected(tool.Name, config.BaseUrl);
        return Task.FromResult(handle);
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        if (_connections.TryGetValue(handle.ConnectionId, out var connection)
            && connection.Handle == handle
            && _connections.TryRemove(handle.ConnectionId, out _))
            LogDirectHttpToolDisconnected(handle.ToolName);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!_connections.TryGetValue(handle.ConnectionId, out var connection)
            || connection.Handle != handle
            || !string.Equals(invocation.ToolName, connection.Handle.ToolName, StringComparison.Ordinal))
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                ErrorCode = "invalid-tool-connection",
                Error = "Direct HTTP connection is missing, disconnected, or does not match the requested tool."
            };

        try
        {
            var baseUrl = connection.Config.BaseUrl.TrimEnd('/');
            var method = invocation.Method.TrimStart('/');

            // Reject path traversal, absolute URLs, and encoded variants to prevent SSRF
            if (method.Contains("..", StringComparison.Ordinal) ||
                method.Contains("://", StringComparison.Ordinal) ||
                method.Contains('\\') ||
                method.Contains('%') ||
                method.Contains('@'))
            {
                throw new ArgumentException($"Invalid method path: '{invocation.Method}'");
            }

            var url = new Uri(new Uri(baseUrl + "/"), method).AbsoluteUri;

            var bytes = JsonSerializer.SerializeToUtf8Bytes(invocation.Parameters, DirectHttpToolJsonContext.Default.DictionaryStringString);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            if (!string.IsNullOrWhiteSpace(connection.Config.AuthHeader))
                request.Headers.TryAddWithoutValidation("Authorization", connection.Config.AuthHeader);

            using var response = await httpClient.SendAsync(request, ct);
            var output = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                LogDirectHttpToolInvocationFailed(new HttpRequestException($"HTTP {(int)response.StatusCode}"), handle.ToolName);
                return new ToolResult { Success = false, ToolName = handle.ToolName, Error = $"HTTP {(int)response.StatusCode}: {output}", Duration = sw.Elapsed };
            }

            return new ToolResult { Success = true, ToolName = handle.ToolName, Output = output, Duration = sw.Elapsed };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or JsonException or IOException or UriFormatException or ArgumentException)
        {
            sw.Stop();
            LogDirectHttpToolInvocationFailed(ex, handle.ToolName);
            return new ToolResult { Success = false, ToolName = handle.ToolName, Error = ex.Message, Duration = sw.Elapsed };
        }
    }

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = $"Direct HTTP tool: {handle.ToolName}",
            Parameters =
            [
                new ToolParameter { Name = "endpoint", Type = "string", Description = "Method path appended to base URL", Required = true }
            ]
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Direct HTTP tool '{Tool}' connected to '{BaseUrl}'")]
    private partial void LogDirectHttpToolConnected(string tool, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Direct HTTP tool '{Tool}' disconnected")]
    private partial void LogDirectHttpToolDisconnected(string tool);

    [LoggerMessage(Level = LogLevel.Error, Message = "Direct HTTP tool invocation failed for '{Tool}'")]
    private partial void LogDirectHttpToolInvocationFailed(Exception ex, string tool);
}
