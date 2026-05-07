using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Marketplace;

public sealed class BrowseMarketplaceItemsActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveItems_ReturnsTypedSummaries()
    {
        const string body = """
        [
          { "itemId": "mp-1", "name": "Alpha", "description": "first", "category": "Integration", "version": "1.0", "author": "ada", "status": "Published", "tags": ["a","b"], "installCount": 5, "rating": 4.5, "ratingCount": 2, "templateId": "tpl-1" },
          { "itemId": "mp-2", "name": "Beta", "description": "second", "category": "ToolConnector", "version": "2.0", "author": "ben", "status": "Draft", "installCount": 0, "rating": 0, "ratingCount": 0 }
        ]
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new BrowseMarketplaceItemsAction(client);

        var result = await action.ExecuteAsync(new BrowseMarketplaceItemsInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.Count.ShouldBe(2);
        result.Value.Items[0].ItemId.ShouldBe("mp-1");
        result.Value.Items[0].Tags.ShouldBe(["a", "b"]);
        result.Value.Items[0].TemplateId.ShouldBe("tpl-1");
        result.Value.Items[1].TemplateId.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArray_ReturnsSuccessWithEmptyList()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new BrowseMarketplaceItemsAction(client);

        var result = await action.ExecuteAsync(new BrowseMarketplaceItemsInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new BrowseMarketplaceItemsAction(client);

        var result = await action.ExecuteAsync(new BrowseMarketplaceItemsInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsMarketplaceEndpoint()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new BrowseMarketplaceItemsAction(client);

        await action.ExecuteAsync(new BrowseMarketplaceItemsInput(), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/marketplace");
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
