using System.Text.Json.Serialization;

namespace Weave.Tools.Connectors;

internal sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public Dictionary<string, string>? Params { get; init; }
}

[JsonSerializable(typeof(JsonRpcRequest))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
