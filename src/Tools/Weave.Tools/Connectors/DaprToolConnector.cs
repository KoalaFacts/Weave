using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

/// <summary>
/// HTTP-based Dapr tool connector — no Dapr SDK required. Invokes Dapr service
/// methods via the sidecar HTTP API. Activated when a "dapr" plugin is configured.
/// </summary>
public sealed partial class DaprToolConnector(HttpClient httpClient, ILogger<DaprToolConnector> logger) : IToolConnector
{
    public ToolType ToolType => ToolType.Dapr;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var dapr = tool.Dapr ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no Dapr configuration");
        LogDaprToolConnected(tool.Name, dapr.AppId);
        return Task.FromResult(new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.Dapr,
            ConnectionId = $"dapr:{dapr.AppId}",
            IsConnected = true
        });
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        LogDaprToolDisconnected(handle.ToolName);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var appId = handle.ConnectionId.Replace("dapr:", "", StringComparison.Ordinal);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(invocation.Parameters, ToolJsonContext.Default.DictionaryStringString);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await httpClient.PostAsync(
                $"/v1.0/invoke/{appId}/method/{invocation.Method}", content, ct);
            response.EnsureSuccessStatusCode();
            var output = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            return new ToolResult { Success = true, ToolName = handle.ToolName, Output = output, Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogDaprToolInvocationFailed(ex, handle.ToolName);
            return new ToolResult { Success = false, ToolName = handle.ToolName, Error = ex.Message, Duration = sw.Elapsed };
        }
    }

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = $"Dapr service invocation tool: {handle.ToolName}"
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dapr tool '{Tool}' connected to app '{AppId}'")]
    private partial void LogDaprToolConnected(string tool, string appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dapr tool '{Tool}' disconnected")]
    private partial void LogDaprToolDisconnected(string tool);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dapr tool invocation failed for '{Tool}'")]
    private partial void LogDaprToolInvocationFailed(Exception ex, string tool);
}
