using System.Text.Json;
using Weave.Invocations;

namespace Weave.Tools.Tests;

public sealed class FileWriteApprovalSerializationTests
{
    [Fact]
    public void Serialize_ApprovedPlan_PreservesBrandedIdAsCanonicalString()
    {
        var id = InvocationId.From("1234567890abcdef1234567890abcdef");
        var plan = new FileWriteApprovalPlan(id, "ws", "writer", "files", "/controlled/root", "note.txt",
            "exact note", 1_048_576, "filesystem/write_file/v1");
        var serialized = FileWritePlanBinding.Serialize(plan);
        using var json = JsonDocument.Parse(serialized);
        var identity = json.RootElement.GetProperty("InvocationId");
        identity.ValueKind.ShouldBe(JsonValueKind.String,
            "The JSON generator must not treat a separately generated branded ID as an empty struct.");
        identity.GetString().ShouldBe(id.ToString());
        var recovered = JsonSerializer.Deserialize(serialized, ApprovalPlanJsonContext.ForStorage.FileWriteApprovalPlan)
            .ShouldNotBeNull();
        recovered.ShouldBe(plan);
        FileWritePlanBinding.Digest(recovered).ShouldBe(FileWritePlanBinding.Digest(plan));
    }
}
