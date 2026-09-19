using System.Text.Json.Serialization;
using Weave.Security.Tokens;

namespace Weave.Silo.Invocations;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CapabilityToken))]
[JsonSerializable(typeof(InvokeToolHttpRequest))]
[JsonSerializable(typeof(InvocationHttpResult))]
[JsonSerializable(typeof(ApprovalHttpStatus))]
[JsonSerializable(typeof(InvocationHttpError))]
internal sealed partial class InvocationHttpJsonContext : JsonSerializerContext;
