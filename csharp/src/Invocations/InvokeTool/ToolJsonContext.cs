using System.Text.Json.Serialization;

namespace Weave.Tools;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
