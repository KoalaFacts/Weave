using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Marketplace;

public sealed class GetMarketplaceItemActionTests
{
    [Fact]
    public async Task ExecuteAsync_FoundItem_ReturnsTypedSummary()
    {
        const string body = """{"itemId":"mp-1","name":"Alpha","description":"d","category":"x","version":"1","author":"a","status":"Published","installCount":7}""";
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new GetMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(new GetMarketplaceItemInput("mp-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Item.ItemId.ShouldBe("mp-1");
        result.Value.Item.InstallCount.ShouldBe(7);
    }

    [Fact]
    public async Task ExecuteAsync_NotFound_ReturnsNotFound()
    {
        using var client = HttpClientReturning(HttpStatusCode.NotFound, string.Empty);
        var action = new GetMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(new GetMarketplaceItemInput("missing"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
        result.Failure.Message.ShouldContain("missing");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsItemEndpointWithEscapedId()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.NotFound, string.Empty);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new GetMarketplaceItemAction(client);

        await action.ExecuteAsync(new GetMarketplaceItemInput("mp/with spaces"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/marketplace/mp%2Fwith%20spaces");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new GetMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(new GetMarketplaceItemInput("mp-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
