using System.Text.Json.Serialization;

namespace Weave.Cli.Shell;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WorkspaceRegistryJsonContext : JsonSerializerContext;
