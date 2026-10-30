using System.Text.Json;
using Weave.Invocations;

namespace Weave.Tools.Tests;

public sealed class InvocationApprovalReviewOwnershipTests
{
    [Fact]
    public void Deserialize_VerifiedReview_ParametersCannotBeChangedUnderItsDigest()
    {
        var json = JsonSerializer.Serialize(new
        {
            InvocationId = "abcdef0123456789abcdef0123456789",
            WorkspaceId = "workspace",
            Subject = "requester",
            ToolName = "files",
            Operation = "write_file",
            TargetDescription = "registered target",
            Parameters = new Dictionary<string, string> { ["path"] = "note.txt" },
            RawInput = "reviewed text",
            PlanDigest = "test-plan",
            ExpiresAt = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
        });
        var review = JsonSerializer.Deserialize<InvocationApprovalReview>(json).ShouldNotBeNull();
        var dictionary = ((object)review.Parameters).ShouldBeAssignableTo<IDictionary<string, string>>();

        dictionary.IsReadOnly.ShouldBeTrue("Verified content must not be mutable while retaining the original digest.");
        Should.Throw<NotSupportedException>(() => dictionary["path"] = "changed.txt");
        review.Parameters["path"].ShouldBe("note.txt");
        review.PlanDigest.ShouldBe("test-plan");
        var roundTrip = JsonSerializer.Deserialize<InvocationApprovalReview>(JsonSerializer.Serialize(review)).ShouldNotBeNull();
        roundTrip.Parameters["path"].ShouldBe("note.txt");
        roundTrip.PlanDigest.ShouldBe(review.PlanDigest);
    }
}
