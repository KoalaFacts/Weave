using System.Text.Json.Serialization;

namespace Weave.Invocations;

[JsonSerializable(typeof(FileWriteApprovalPlan))]
internal partial class ApprovalPlanJsonContext : JsonSerializerContext
{
}
