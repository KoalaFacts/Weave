using System.Text.Json.Serialization;

namespace Weave.Tools.Connectors;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DirectHttpToolJsonContext : JsonSerializerContext;
