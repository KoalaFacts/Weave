using System.Net;
using System.Text.Json;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceSubmitPromptFlowTests
{
    [Theory]
    [InlineData(" local , , review ", new[] { "local", "review" })]
    [InlineData("", new string[] { })]
    public async Task Submit_ActualPromptsProduceExpectedJson_AndRenderReturnedIdentity(string tags, string[] expectedTags)
    {
        using var console = new MarketplacePromptConsole();
        using var fixture = new MarketplaceWriteFlowFixture();
        MarketplaceWriteFlowFixture.QueueSubmission(console, tags);
        fixture.ConfigureResponse(HttpStatusCode.Created, "Submitted");

        var result = await fixture.ExecuteAsync("submit", TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Requests.ShouldBe(["GET /health", "POST /api/marketplace"]);
        using var body = JsonDocument.Parse(fixture.Body.ShouldNotBeNull());
        var json = body.RootElement;
        json.GetProperty("name").GetString().ShouldBe("Local review [literal]");
        json.GetProperty("description").GetString().ShouldBe("Review documents locally.");
        json.GetProperty("category").GetString().ShouldBe("AgentSkill");
        json.GetProperty("version").GetString().ShouldBe("1.0.0");
        json.GetProperty("author").GetString().ShouldBe("Fixture author");
        json.GetProperty("tags").EnumerateArray().Select(value => value.GetString()).ShouldBe(expectedTags);
        console.RemainingKeys.ShouldBe(0);
        console.Text.ShouldContain("Item 'Server item [literal]' submitted (ID: item?owned, status: Submitted).");
        console.Text.ShouldContain("Submit a security review with 'weave marketplace publish'");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Marketplace rejected submission: 400.")]
    [InlineData(HttpStatusCode.Forbidden, "Silo refused submission: 403.")]
    public async Task Submit_RejectedAfterRealPrompts_ReturnsSpecificErrorWithoutSuccess(HttpStatusCode status, string expected)
    {
        using var console = new MarketplacePromptConsole();
        using var fixture = new MarketplaceWriteFlowFixture();
        MarketplaceWriteFlowFixture.QueueSubmission(console);
        fixture.ConfigureResponse(status);

        (await fixture.ExecuteAsync("submit", TestContext.Current.CancellationToken)).ShouldBe(1);

        fixture.Requests.ShouldBe(["GET /health", "POST /api/marketplace"]);
        console.RemainingKeys.ShouldBe(0);
        console.Text.ShouldContain(expected);
        console.Text.ShouldNotContain("submitted (ID:");
        console.Text.ShouldNotContain("Submit a security review");
    }
}
