using System.Text.Json.Serialization;

namespace Weave.Cli.Shell;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
internal sealed partial class CliConfigJsonContext : JsonSerializerContext;
