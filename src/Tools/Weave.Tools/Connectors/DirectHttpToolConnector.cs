using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

/// <summary>
/// HTTP-based tool connector — calls remote services directly by URL.
/// No sidecar or service mesh required. A lightweight alternative to
/// <see cref="DaprToolConnector"/> for environments without Dapr.
/// </summary>
public sealed partial class DirectHttpToolConnector(HttpClient httpClient, ILogger<DirectHttpToolConnector> logger) : IToolConnector, IDisposable
{
    private readonly ConcurrentDictionary<string, string> _authHeaders = new(StringComparer.Ordinal);

    // HttpClient lifetime is owned by IHttpClientFactory; do not dispose.
    void IDisposable.Dispose() { }

    public ToolType ToolType => ToolType.DirectHttp;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var config = tool.DirectHttp ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no DirectHttp configuration");

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new InvalidOperationException($"Tool '{tool.Name}': DirectHttp 'base_url' is required");

        // Store auth per-tool, applied per-request in InvokeAsync.
        // Never use DefaultRequestHeaders — the HttpClient is shared across tools.
        if (!string.IsNullOrWhiteSpace(config.AuthHeader))
            _authHeaders[tool.Name] = config.AuthHeader;
        else
            _authHeaders.TryRemove(tool.Name, out _);

        LogDirectHttpToolConnected(tool.Name, config.BaseUrl);

        return Task.FromResult(new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.DirectHttp,
            ConnectionId = config.BaseUrl,
            IsConnected = true
        });
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        _authHeaders.TryRemove(handle.ToolName, out _);
        LogDirectHttpToolDisconnected(handle.ToolName);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var baseUrl = handle.ConnectionId.TrimEnd('/');
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

            var bytes = JsonSerializer.SerializeToUtf8Bytes(invocation.Parameters, ToolJsonContext.Default.DictionaryStringString);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            if (_authHeaders.TryGetValue(handle.ToolName, out var authHeader))
                request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var output = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            return new ToolResult { Success = true, ToolName = handle.ToolName, Output = output, Duration = sw.Elapsed };
        }
        catch (Exception ex)
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
