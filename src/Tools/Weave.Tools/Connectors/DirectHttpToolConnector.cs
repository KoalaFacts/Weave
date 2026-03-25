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
public sealed partial class DirectHttpToolConnector(HttpClient httpClient, ILogger<DirectHttpToolConnector> logger) : IToolConnector
{
    public ToolType ToolType => ToolType.DirectHttp;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var config = tool.DirectHttp ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no DirectHttp configuration");

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new InvalidOperationException($"Tool '{tool.Name}': DirectHttp 'base_url' is required");

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
            var url = $"{baseUrl}/{method}";

            var bytes = JsonSerializer.SerializeToUtf8Bytes(invocation.Parameters, ToolJsonContext.Default.DictionaryStringString);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await httpClient.PostAsync(url, content, ct);
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
