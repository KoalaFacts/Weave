using System.Text.Json;

namespace Weave.Tools.Connectors;

internal static class OpenApiSpecParser
{
    private static readonly string[] _httpMethods = ["get", "post", "put", "patch", "delete", "head", "options"];

    public static OpenApiSpec Parse(string specJson, string? fallbackBaseUrl)
    {
        using var doc = JsonDocument.Parse(specJson);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("OpenAPI spec root must be a JSON object.");

        var baseUrl = ExtractBaseUrl(root, fallbackBaseUrl);
        var operations = ExtractOperations(root);

        if (operations.Count == 0)
            throw new InvalidOperationException("OpenAPI spec defines no operations under 'paths'.");

        return new OpenApiSpec { BaseUrl = baseUrl, Operations = operations };
    }

    private static string ExtractBaseUrl(JsonElement root, string? fallbackBaseUrl)
    {
        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                if (server.TryGetProperty("url", out var urlElement) &&
                    urlElement.ValueKind == JsonValueKind.String &&
                    urlElement.GetString() is { Length: > 0 } url)
                {
                    return url.TrimEnd('/');
                }
            }
        }

        if (!string.IsNullOrEmpty(fallbackBaseUrl))
            return fallbackBaseUrl.TrimEnd('/');

        throw new InvalidOperationException(
            "OpenAPI spec has no 'servers' entry with a URL, and no fallback base URL was derived from the spec location.");
    }

    private static List<OpenApiOperation> ExtractOperations(JsonElement root)
    {
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            return [];

        var operations = new List<OpenApiOperation>();
        foreach (var pathEntry in paths.EnumerateObject())
        {
            if (pathEntry.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var methodName in _httpMethods)
            {
                if (!pathEntry.Value.TryGetProperty(methodName, out var operation) ||
                    operation.ValueKind != JsonValueKind.Object)
                    continue;

                operations.Add(ExtractOperation(pathEntry.Name, methodName, operation));
            }
        }
        return operations;
    }

    private static OpenApiOperation ExtractOperation(string path, string method, JsonElement operation)
    {
        var operationId = operation.TryGetProperty("operationId", out var idElement) &&
            idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? FallbackOperationId(method, path)
            : FallbackOperationId(method, path);

        var summary = operation.TryGetProperty("summary", out var summaryElement) &&
            summaryElement.ValueKind == JsonValueKind.String
            ? summaryElement.GetString()
            : null;

        var parameters = ExtractParameters(operation);
        var hasRequestBody = operation.TryGetProperty("requestBody", out var bodyElement) &&
            bodyElement.ValueKind == JsonValueKind.Object;

        return new OpenApiOperation
        {
            OperationId = operationId,
            HttpMethod = method.ToUpperInvariant(),
            PathTemplate = path,
            Summary = summary,
            Parameters = parameters,
            HasRequestBody = hasRequestBody
        };
    }

    private static List<OpenApiParameter> ExtractParameters(JsonElement operation)
    {
        if (!operation.TryGetProperty("parameters", out var paramsArray) ||
            paramsArray.ValueKind != JsonValueKind.Array)
            return [];

        var parameters = new List<OpenApiParameter>();
        foreach (var param in paramsArray.EnumerateArray())
        {
            if (param.ValueKind != JsonValueKind.Object)
                continue;
            if (!param.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            if (!param.TryGetProperty("in", out var inEl) || inEl.ValueKind != JsonValueKind.String)
                continue;

            parameters.Add(new OpenApiParameter
            {
                Name = nameEl.GetString()!,
                In = inEl.GetString()!,
                Required = param.TryGetProperty("required", out var reqEl) &&
                    reqEl.ValueKind == JsonValueKind.True
            });
        }
        return parameters;
    }

    private static string FallbackOperationId(string method, string path)
    {
        var slug = path.Replace('/', '_').Replace('{', '_').Replace('}', '_').Trim('_');
        return $"{method}_{slug}";
    }
}

internal sealed record OpenApiSpec
{
    public required string BaseUrl { get; init; }
    public required IReadOnlyList<OpenApiOperation> Operations { get; init; }
}
