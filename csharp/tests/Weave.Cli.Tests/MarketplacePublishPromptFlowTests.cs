using System.Net;
using System.Text.Json;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplacePublishPromptFlowTests
{
    [Theory]
    [InlineData(true, "Approved after review.", "Published")]
    [InlineData(false, "", "Rejected")]
    [InlineData(true, "Server review required.", "PendingReview")]
    public async Task Publish_ActualReviewPromptsSendDecision_AndRenderServerStatus(bool approved, string notes, string state)
    {
        using var console = new MarketplacePromptConsole();
        using var fixture = new MarketplaceWriteFlowFixture();
        MarketplaceWriteFlowFixture.QueueReview(console, approved, notes);
        fixture.ConfigureResponse(HttpStatusCode.OK, state);

        (await fixture.ExecuteAsync("publish", TestContext.Current.CancellationToken)).ShouldBe(0);

        fixture.Requests.ShouldBe(["GET /health", "POST /api/marketplace/item%3Fowned/publish"]);
        using var body = JsonDocument.Parse(fixture.Body.ShouldNotBeNull());
        body.RootElement.GetProperty("reviewerId").GetString().ShouldBe("fixture-reviewer");
        body.RootElement.GetProperty("approved").GetBoolean().ShouldBe(approved);
        if (notes.Length == 0)
            body.RootElement.TryGetProperty("notes", out _).ShouldBeFalse();
        else
            body.RootElement.GetProperty("notes").GetString().ShouldBe(notes);
        console.RemainingKeys.ShouldBe(0);
        if (state == "Published")
        {
            console.Text.ShouldContain("Item 'Server item [literal]' is now published in the marketplace.");
            console.Text.ShouldNotContain("was not published");
        }
        else
        {
            console.Text.ShouldContain($"Item 'Server item [literal]' was not published (status: {state}).");
            console.Text.ShouldNotContain("is now published");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Failed to publish: Silo refused publish: 403.")]
    [InlineData(HttpStatusCode.NotFound, "Failed to publish: Item 'item?owned' not found.")]
    public async Task Publish_ServerRefusesDecision_ReturnsFailureWithoutPublishedMessage(HttpStatusCode status, string expected)
    {
        using var console = new MarketplacePromptConsole();
        using var fixture = new MarketplaceWriteFlowFixture();
        MarketplaceWriteFlowFixture.QueueReview(console, true, "Owned test review.");
        fixture.ConfigureResponse(status);

        (await fixture.ExecuteAsync("publish", TestContext.Current.CancellationToken)).ShouldBe(1);

        fixture.Requests.ShouldBe(["GET /health", "POST /api/marketplace/item%3Fowned/publish"]);
        console.RemainingKeys.ShouldBe(0);
        console.Text.ShouldContain(expected);
        console.Text.ShouldNotContain("is now published");
        console.Text.ShouldNotContain("was not published (status:");
    }
}
