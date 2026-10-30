using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Marketplace;

public sealed class PublishMarketplaceItemActionTests
{
    private static readonly PublishMarketplaceItemInput Sample =
        new("mp-1", "reviewer-1", true, "looks good");

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsTypedSummary()
    {
        const string body = """{"itemId":"mp-1","name":"Alpha","description":"d","category":"c","version":"1","author":"a","status":"Published"}""";
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new PublishMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(Sample, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Item.Status.ShouldBe("Published");
    }

    [Fact]
    public async Task ExecuteAsync_NotFound_ReturnsNotFound()
    {
        using var client = HttpClientReturning(HttpStatusCode.NotFound, string.Empty);
        var action = new PublishMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(Sample, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
    }

    [Fact]
    public async Task ExecuteAsync_BadRequest_ReturnsValidationFailed()
    {
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, string.Empty);
        var action = new PublishMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(Sample, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsPublishEndpointWithPostMethod()
    {
        const string body = """{"itemId":"mp-1","name":"Alpha","description":"d","category":"c","version":"1","author":"a","status":"Published"}""";
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, body);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new PublishMarketplaceItemAction(client);

        await action.ExecuteAsync(Sample, CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/marketplace/mp-1/publish");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
