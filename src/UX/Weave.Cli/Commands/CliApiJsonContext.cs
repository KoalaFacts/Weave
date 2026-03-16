using System.Text.Json.Serialization;

namespace Weave.Cli.Commands;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiStartWorkspaceRequest))]
[JsonSerializable(typeof(ApiWorkspaceResponse))]
[JsonSerializable(typeof(ApiAgentResponse))]
[JsonSerializable(typeof(ApiTaskResponse))]
[JsonSerializable(typeof(ApiToolResponse))]
[JsonSerializable(typeof(List<ApiAgentResponse>))]
[JsonSerializable(typeof(List<ApiToolResponse>))]
internal sealed partial class CliApiJsonContext : JsonSerializerContext;
