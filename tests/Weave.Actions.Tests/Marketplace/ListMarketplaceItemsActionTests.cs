using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Marketplace;

public sealed class ListMarketplaceItemsActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveItems_ReturnsOpaqueElements()
    {
        const string body = """[{"itemId":"mp-1"},{"itemId":"mp-2"}]""";
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new ListMarketplaceItemsAction(client);

        var result = await action.ExecuteAsync(new ListMarketplaceItemsInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.Count.ShouldBe(2);
        result.Value.Items[0].GetProperty("itemId").GetString().ShouldBe("mp-1");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new ListMarketplaceItemsAction(client);

        var result = await action.ExecuteAsync(new ListMarketplaceItemsInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsMarketplaceEndpoint()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListMarketplaceItemsAction(client);

        await action.ExecuteAsync(new ListMarketplaceItemsInput(), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/marketplace");
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
