using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Marketplace;

public sealed class SubmitMarketplaceItemActionTests
{
    private static readonly SubmitMarketplaceItemInput Sample =
        new("Alpha", "First", "Integration", "1.0", "ada", ["a", "b"]);

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsTypedSummary()
    {
        const string body = """{"itemId":"mp-1","name":"Alpha","description":"First","category":"Integration","version":"1.0","author":"ada","status":"Draft"}""";
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new SubmitMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(Sample, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Item.ItemId.ShouldBe("mp-1");
        result.Value.Item.Status.ShouldBe("Draft");
    }

    [Fact]
    public async Task ExecuteAsync_BadRequest_ReturnsValidationFailed()
    {
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, string.Empty);
        var action = new SubmitMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(Sample, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
    }

    [Fact]
    public async Task ExecuteAsync_Unauthorized_ReturnsUnauthorized()
    {
        using var client = HttpClientReturning(HttpStatusCode.Unauthorized, string.Empty);
        var action = new SubmitMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(Sample, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Unauthorized);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsMarketplacePostEndpoint()
    {
        const string body = """{"itemId":"mp-1","name":"Alpha","description":"d","category":"c","version":"1","author":"a","status":"Draft"}""";
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, body);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new SubmitMarketplaceItemAction(client);

        await action.ExecuteAsync(Sample, CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/marketplace");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
