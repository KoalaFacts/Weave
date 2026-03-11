using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed class OpenApiToolConnector(HttpClient httpClient, ILogger<OpenApiToolConnector> logger) : IToolConnector
{
    public ToolType ToolType => ToolType.OpenApi;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var openApi = tool.OpenApi ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no OpenAPI configuration");

        if (openApi.Auth is { Type: "bearer" })
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", openApi.Auth.Token);
        }

        logger.LogInformation("OpenAPI tool '{Tool}' connected to '{Spec}'", tool.Name, openApi.SpecUrl);

        return Task.FromResult(new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.OpenApi,
            ConnectionId = $"openapi:{tool.Name}",
            IsConnected = true
        });
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var endpoint = invocation.Parameters.GetValueOrDefault("endpoint", "/");
            var method = invocation.Parameters.GetValueOrDefault("http_method", "GET");

            HttpResponseMessage response;
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = invocation.RawInput ?? "{}";
                response = await httpClient.PostAsync(endpoint,
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
            }
            else
            {
                response = await httpClient.GetAsync(endpoint, ct);
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            return new ToolResult
            {
                Success = response.IsSuccessStatusCode,
                ToolName = handle.ToolName,
                Output = content,
                Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}",
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
            Description = $"OpenAPI tool: {handle.ToolName}",
            Parameters =
            [
                new ToolParameter { Name = "endpoint", Type = "string", Description = "API endpoint path", Required = true },
                new ToolParameter { Name = "http_method", Type = "string", Description = "HTTP method (GET, POST, etc.)", Required = false }
            ]
        });
    }
}
