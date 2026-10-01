using System.Text.Json.Serialization;

namespace Weave.Cli.Commands.Local;

[JsonSerializable(typeof(LocalDeployment))]
internal sealed partial class LocalJsonContext : JsonSerializerContext;
