using System.Text.Json.Serialization;

namespace Weave.Cli.Commands;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateCache))]
internal sealed partial class VersionJsonContext : JsonSerializerContext;
