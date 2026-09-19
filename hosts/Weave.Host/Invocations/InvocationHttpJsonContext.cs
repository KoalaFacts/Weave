using System.Text.Json.Serialization;
using Weave.Security.Tokens;
using Weave.Tools.Tool;

namespace Weave.Silo.Invocations;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CapabilityToken))]
[JsonSerializable(typeof(ToolInvocation))]
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(ApprovalHttpStatus))]
[JsonSerializable(typeof(InvocationHttpError))]
internal sealed partial class InvocationHttpJsonContext : JsonSerializerContext;
