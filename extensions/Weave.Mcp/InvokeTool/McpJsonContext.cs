using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Weave.Tools.Connectors;

internal sealed record McpJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonNode? Params { get; init; }
}

internal sealed record McpJsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonNode? Params { get; init; }
}

internal sealed record McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required McpClientCapabilities Capabilities { get; init; }

    [JsonPropertyName("clientInfo")]
    public required McpClientInfo ClientInfo { get; init; }
}

internal sealed record McpClientCapabilities;

internal sealed record McpClientInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

internal sealed record McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string? ProtocolVersion { get; init; }

    [JsonPropertyName("serverInfo")]
    public McpServerInfo? ServerInfo { get; init; }
}

internal sealed record McpServerInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

internal sealed record McpToolListResult
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<McpTool> Tools { get; init; } = [];
}

internal sealed record McpTool
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public JsonElement? InputSchema { get; init; }
}

internal sealed record McpToolCallParams
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required JsonNode Arguments { get; init; }
}

internal sealed record McpToolCallResult
{
    [JsonPropertyName("content")]
    public IReadOnlyList<McpContentBlock> Content { get; init; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

internal sealed record McpContentBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

[JsonSerializable(typeof(McpJsonRpcRequest))]
[JsonSerializable(typeof(McpJsonRpcNotification))]
[JsonSerializable(typeof(McpInitializeParams))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpToolListResult))]
[JsonSerializable(typeof(McpToolCallParams))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class McpJsonContext : JsonSerializerContext;
