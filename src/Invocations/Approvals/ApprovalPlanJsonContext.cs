using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Invocations;

[JsonSerializable(typeof(FileWriteApprovalPlan))]
internal partial class ApprovalPlanJsonContext : JsonSerializerContext
{
    // The JSON generator cannot inspect another generator's output in the same
    // compilation. Reuse the generated ID converter through runtime options.
    internal static ApprovalPlanJsonContext ForStorage { get; } = new(new JsonSerializerOptions
    {
        Converters = { new InvocationIdJsonConverter() }
    });
}
