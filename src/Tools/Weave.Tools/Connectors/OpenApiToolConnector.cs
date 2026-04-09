using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed partial class OpenApiToolConnector(HttpClient httpClient, ILogger<OpenApiToolConnector> logger) : IToolConnector
{
    private readonly ConcurrentDictionary<string, (string BaseUrl, string? AuthHeader)> _toolConfigs = new(StringComparer.Ordinal);

    public ToolType ToolType => ToolType.OpenApi;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var openApi = tool.OpenApi ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no OpenAPI configuration");

        string? baseUrl = null;
        if (Uri.TryCreate(openApi.SpecUrl, UriKind.Absolute, out var specUri))
            baseUrl = specUri.GetLeftPart(UriPartial.Authority);

        string? authHeader = null;
        if (openApi.Auth is { Type: "bearer" })
            authHeader = $"Bearer {openApi.Auth.Token}";

        _toolConfigs[tool.Name] = (baseUrl ?? "", authHeader);

        LogOpenApiToolConnected(tool.Name, openApi.SpecUrl);

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
        _toolConfigs.TryRemove(handle.ToolName, out _);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var endpoint = invocation.Parameters.GetValueOrDefault("endpoint", "/");
            var method = invocation.Parameters.GetValueOrDefault("http_method", "GET");

            // Reject path traversal, absolute URLs, and encoded variants to prevent SSRF
            if (endpoint.Contains("..", StringComparison.Ordinal) ||
                endpoint.Contains("://", StringComparison.Ordinal) ||
                endpoint.Contains('\\') ||
                endpoint.Contains('%') ||
                endpoint.Contains('@'))
            {
                throw new ArgumentException($"Invalid endpoint path: '{endpoint}'");
            }

            if (!_toolConfigs.TryGetValue(handle.ToolName, out var config))
                throw new InvalidOperationException($"Tool '{handle.ToolName}' is not connected.");

            var baseUrl = config.BaseUrl.TrimEnd('/');
            var path = endpoint.TrimStart('/');
            var url = string.IsNullOrEmpty(baseUrl) ? $"/{path}" : $"{baseUrl}/{path}";

            HttpResponseMessage response;
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = invocation.RawInput ?? "{}";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                if (config.AuthHeader is not null)
                    request.Headers.TryAddWithoutValidation("Authorization", config.AuthHeader);
                response = await httpClient.SendAsync(request, ct);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (config.AuthHeader is not null)
                    request.Headers.TryAddWithoutValidation("Authorization", config.AuthHeader);
                response = await httpClient.SendAsync(request, ct);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenAPI tool '{Tool}' connected to '{Spec}'")]
    private partial void LogOpenApiToolConnected(string tool, string spec);
}
