using System.Diagnostics;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed class DaprToolConnector(DaprClient daprClient, ILogger<DaprToolConnector> logger) : IToolConnector
{
    public ToolType ToolType => ToolType.Dapr;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var dapr = tool.Dapr ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no Dapr configuration");

        logger.LogInformation("Dapr tool '{Tool}' connected to app '{AppId}'", tool.Name, dapr.AppId);

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
        logger.LogInformation("Dapr tool '{Tool}' disconnected", handle.ToolName);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var appId = handle.ConnectionId.Replace("dapr:", "");
            var response = await daprClient.InvokeMethodAsync<Dictionary<string, string>, string>(
                appId, invocation.Method, invocation.Parameters, ct);
            sw.Stop();

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
            logger.LogError(ex, "Dapr tool invocation failed for '{Tool}'", handle.ToolName);
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
            Description = $"Dapr service invocation tool: {handle.ToolName}"
        });
    }
}
