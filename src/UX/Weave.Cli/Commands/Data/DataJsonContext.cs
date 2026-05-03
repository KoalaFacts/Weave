using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Cli.Commands;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkspaceExport))]
[JsonSerializable(typeof(List<JsonElement>))]
internal sealed partial class DataJsonContext : JsonSerializerContext;
