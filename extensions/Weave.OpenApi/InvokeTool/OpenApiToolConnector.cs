using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Tool;

namespace Weave.Tools.Connectors;

public sealed partial class OpenApiToolConnector(HttpClient httpClient, ILogger<OpenApiToolConnector> logger) : IToolConnector
{
    private readonly ConcurrentDictionary<string, OpenApiConnection> _connections = new(StringComparer.Ordinal);

    public ToolType ToolType => ToolType.OpenApi;

    public async Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var openApi = tool.OpenApi ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no OpenAPI configuration");

        var specJson = await LoadSpecAsync(openApi.SpecUrl, ct);
        var fallbackBaseUrl = ExtractAuthority(openApi.SpecUrl);
        var spec = OpenApiSpecParser.Parse(specJson, fallbackBaseUrl);

        var authHeader = openApi.Auth is { Type: "bearer", Token: { Length: > 0 } bearerToken }
            ? $"Bearer {bearerToken}"
            : null;

        var connectionId = $"openapi:{tool.Name}";
        _connections[connectionId] = new OpenApiConnection
        {
            BaseUrl = spec.BaseUrl,
            Operations = spec.Operations.ToDictionary(o => o.OperationId, StringComparer.Ordinal),
            AuthHeader = authHeader
        };

        LogOpenApiToolConnected(tool.Name, openApi.SpecUrl, spec.Operations.Count);

        return new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.OpenApi,
            ConnectionId = connectionId,
            IsConnected = true
        };
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        _connections.TryRemove(handle.ConnectionId, out _);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(handle.ConnectionId, out var connection))
            return new ToolResult { Success = false, ToolName = handle.ToolName, Error = $"OpenAPI tool '{handle.ToolName}' is not connected." };

        if (!connection.Operations.TryGetValue(invocation.Method, out var operation))
        {
            var available = string.Join(", ", connection.Operations.Keys);
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = $"Operation '{invocation.Method}' not found. Available: {available}"
            };
        }

        return await OpenApiInvoker.InvokeAsync(httpClient, connection.BaseUrl, operation, invocation, connection.AuthHeader, ct);
    }

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(handle.ConnectionId, out var connection))
        {
            return Task.FromResult(new ToolSchema
            {
                ToolName = handle.ToolName,
                Description = $"OpenAPI tool '{handle.ToolName}' not connected"
            });
        }

        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = FormatMenu(handle.ToolName, connection.Operations.Values),
            Parameters =
            [
                new ToolParameter
                {
                    Name = "method",
                    Type = "string",
                    Description = $"operationId to invoke. Available: {string.Join(", ", connection.Operations.Keys)}",
                    Required = true
                }
            ]
        });
    }

    private async Task<string> LoadSpecAsync(string specUrl, CancellationToken ct)
    {
        if (Uri.TryCreate(specUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var response = await httpClient.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        if (uri is { Scheme: "file" })
            return await File.ReadAllTextAsync(uri.LocalPath, ct);

        if (File.Exists(specUrl))
            return await File.ReadAllTextAsync(specUrl, ct);

        throw new InvalidOperationException($"OpenAPI spec URL '{specUrl}' is not an http(s) URL and does not point to an existing file.");
    }

    private static string? ExtractAuthority(string specUrl) =>
        Uri.TryCreate(specUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    private static string FormatMenu(string toolName, IEnumerable<OpenApiOperation> operations)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var op in operations)
        {
            if (first)
                sb.Append("OpenAPI tool '").Append(toolName).Append("' exposes: ");
            else
                sb.Append("; ");
            first = false;

            sb.Append(op.OperationId).Append(" (").Append(op.HttpMethod).Append(' ').Append(op.PathTemplate).Append(')');
            if (!string.IsNullOrEmpty(op.Summary))
                sb.Append(" — ").Append(op.Summary);
        }

        if (first)
            return $"OpenAPI tool '{toolName}' exposes no operations.";

        sb.Append('.');
        return sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenAPI tool '{Tool}' connected to '{Spec}' ({OperationCount} operations)")]
    private partial void LogOpenApiToolConnected(string tool, string spec, int operationCount);

    private sealed record OpenApiConnection
    {
        public required string BaseUrl { get; init; }
        public required IReadOnlyDictionary<string, OpenApiOperation> Operations { get; init; }
        public string? AuthHeader { get; init; }
    }
}
