using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Weave.Tools.Tool;

namespace Weave.Tools.Connectors;

internal static class OpenApiInvoker
{
    public static async Task<ToolResult> InvokeAsync(
        HttpClient httpClient,
        string baseUrl,
        OpenApiOperation operation,
        ToolInvocation invocation,
        string? authHeader,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var consumed = new HashSet<string>(StringComparer.Ordinal);

            var pathBuildResult = BuildPath(operation, invocation, consumed);
            if (pathBuildResult.Error is not null)
            {
                sw.Stop();
                return Fail(invocation.ToolName, pathBuildResult.Error, sw);
            }

            var queryString = BuildQueryString(operation, invocation, consumed);
            var url = $"{baseUrl}{pathBuildResult.Path}{queryString}";

            using var request = new HttpRequestMessage(new HttpMethod(operation.HttpMethod), url);
            if (authHeader is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            AttachHeaders(operation, invocation, consumed, request);

            if (operation.HasRequestBody)
                request.Content = BuildBody(invocation, consumed);

            using var response = await httpClient.SendAsync(request, ct);
            var output = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            return new ToolResult
            {
                Success = response.IsSuccessStatusCode,
                ToolName = invocation.ToolName,
                Output = output,
                Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}: {output}",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            sw.Stop();
            return Fail(invocation.ToolName, ex.Message, sw);
        }
    }

    private static (string? Path, string? Error) BuildPath(
        OpenApiOperation operation,
        ToolInvocation invocation,
        HashSet<string> consumed)
    {
        var path = operation.PathTemplate;
        foreach (var param in operation.Parameters)
        {
            if (!param.In.Equals("path", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!invocation.Parameters.TryGetValue(param.Name, out var value))
            {
                if (param.Required)
                    return (null, $"Missing required path parameter '{param.Name}' for operation '{operation.OperationId}'.");
                continue;
            }

            consumed.Add(param.Name);
            path = path.Replace("{" + param.Name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        if (path.Contains('{', StringComparison.Ordinal) || path.Contains('}', StringComparison.Ordinal))
            return (null, $"Unresolved path placeholders remain in '{path}' for operation '{operation.OperationId}'.");

        return (path, null);
    }

    private static string BuildQueryString(
        OpenApiOperation operation,
        ToolInvocation invocation,
        HashSet<string> consumed)
    {
        var parts = new List<string>();
        foreach (var param in operation.Parameters)
        {
            if (!param.In.Equals("query", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!invocation.Parameters.TryGetValue(param.Name, out var value))
                continue;

            consumed.Add(param.Name);
            parts.Add($"{Uri.EscapeDataString(param.Name)}={Uri.EscapeDataString(value)}");
        }
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static void AttachHeaders(
        OpenApiOperation operation,
        ToolInvocation invocation,
        HashSet<string> consumed,
        HttpRequestMessage request)
    {
        foreach (var param in operation.Parameters)
        {
            if (!param.In.Equals("header", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!invocation.Parameters.TryGetValue(param.Name, out var value))
                continue;

            consumed.Add(param.Name);
            request.Headers.TryAddWithoutValidation(param.Name, value);
        }
    }

    private static HttpContent BuildBody(ToolInvocation invocation, HashSet<string> consumed)
    {
        if (invocation.RawInput is { Length: > 0 } raw)
            return new StringContent(raw, Encoding.UTF8, "application/json");

        var bodyParams = invocation.Parameters
            .Where(kv => !consumed.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(bodyParams, ToolJsonContext.Default.DictionaryStringString);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private static ToolResult Fail(string toolName, string error, Stopwatch sw) => new()
    {
        Success = false,
        ToolName = toolName,
        Error = error,
        Duration = sw.Elapsed
    };
}
