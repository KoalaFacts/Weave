using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Marketplace;

public sealed class SearchMarketplaceActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveResults_ReturnsTypedSummaries()
    {
        const string body = """[{"itemId":"mp-1","name":"Alpha","description":"d","category":"x","version":"1","author":"a","status":"Published"}]""";
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new SearchMarketplaceAction(client);

        var result = await action.ExecuteAsync(new SearchMarketplaceInput("alpha"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.Count.ShouldBe(1);
        result.Value.Items[0].Name.ShouldBe("Alpha");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsSearchEndpointWithEscapedQuery()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new SearchMarketplaceAction(client);

        await action.ExecuteAsync(new SearchMarketplaceInput("git tools"), CancellationToken.None);

        handler.LastRequestUri!.PathAndQuery.ShouldBe("/api/marketplace/search?q=git%20tools");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankQuery_Throws(string query)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new SearchMarketplaceAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new SearchMarketplaceInput(query), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new SearchMarketplaceAction(client);

        var result = await action.ExecuteAsync(new SearchMarketplaceInput("alpha"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
