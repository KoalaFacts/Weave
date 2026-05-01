using System.Text.Json.Serialization;

namespace Weave.Tools.Connectors;

[JsonSerializable(typeof(JsonRpcRequest))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
