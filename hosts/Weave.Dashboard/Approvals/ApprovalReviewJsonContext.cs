using System.Text.Json.Serialization;

namespace Weave.Dashboard.Approvals;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApprovalReviewSnapshot))]
internal sealed partial class ApprovalReviewJsonContext : JsonSerializerContext;
