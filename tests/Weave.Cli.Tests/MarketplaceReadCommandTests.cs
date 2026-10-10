using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceReadCommandTests
{
    private const string Item = """{"itemId":"item-marker","name":"name [literal]","description":"description [literal]","category":"category [literal]","version":"1.2.3","author":"author [literal]","status":"Published","tags":["tag [literal]"],"rating":4.5,"ratingCount":2,"installCount":17}""";

    [Theory]
    [InlineData("list")]
    [InlineData("info")]
    [InlineData("search")]
    public async Task ExecuteAsync_ItemsReturned_RendersLiteralMarkupAndUsesRequestedEndpoint(string command)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceFixture();
        fixture.Respond = path => DataTransferFixture.Response(200, path == "/health" ? ""
            : command == "info" ? Item : "[" + Item + "]");

        (await fixture.ExecuteAsync(command, TestContext.Current.CancellationToken)).ShouldBe(0);

        fixture.Paths.ShouldBe(["/health", Endpoint(command)]);
        output.Text.ShouldContain("name [literal]");
        output.Text.ShouldContain("category [literal]");
        output.Text.ShouldContain("author [literal]");
        if (command != "search")
        {
            output.Text.ShouldContain("1.2.3");
            output.Text.ShouldContain("17");
        }
        if (command != "list")
            output.Text.ShouldContain("description [literal]");
        if (command == "info")
        {
            output.Text.ShouldContain("item-marker");
            output.Text.ShouldContain("tag [literal]");
            output.Text.ShouldContain("2 ratings");
        }
    }

    [Fact]
    public async Task ExecuteAsync_SearchLongDescription_TruncatesDescriptionAtDisplayBoundary()
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceFixture();
        var prefix = new string('x', 57);
        fixture.Respond = path => DataTransferFixture.Response(200, path == "/health" ? ""
            : "[{\"name\":\"long-item\",\"description\":\"" + prefix + "OMITTED_SUFFIX\",\"category\":\"tools\",\"author\":\"writer\"}]");

        (await fixture.ExecuteAsync("search", TestContext.Current.CancellationToken)).ShouldBe(0);

        output.Text.ShouldContain(prefix + "...");
        output.Text.ShouldNotContain("OMITTED_SUFFIX");
        fixture.Paths.ShouldBe(["/health", Endpoint("search")]);
    }

    [Theory]
    [InlineData("list", "No published items in the marketplace.")]
    [InlineData("search", "No marketplace items matching 'query & marker'.")]
    public async Task ExecuteAsync_EmptyResults_ReportsNoMatches(string command, string message)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceFixture();
        fixture.Respond = path => DataTransferFixture.Response(200, path == "/health" ? "" : "[]");

        (await fixture.ExecuteAsync(command, TestContext.Current.CancellationToken)).ShouldBe(0);

        output.Text.ShouldContain(message);
        fixture.Paths.ShouldBe(["/health", Endpoint(command)]);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("info")]
    [InlineData("search")]
    public async Task ExecuteAsync_ServerUnavailable_DoesNotQueryMarketplace(string command)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceFixture();
        fixture.Respond = _ => DataTransferFixture.Response(503);

        (await fixture.ExecuteAsync(command, TestContext.Current.CancellationToken)).ShouldBe(1);

        fixture.Paths.ShouldBe(["/health"]);
        output.Text.ShouldContain("Weave server is not running.");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("info")]
    [InlineData("search")]
    public async Task ExecuteAsync_QueryFails_ReturnsErrorWithDistinctFailureMessage(string command)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceFixture();
        fixture.Respond = path => path == "/health" ? DataTransferFixture.Response(200)
            : throw new HttpRequestException("market-query-marker");

        (await fixture.ExecuteAsync(command, TestContext.Current.CancellationToken)).ShouldBe(1);

        fixture.Paths.ShouldBe(["/health", Endpoint(command)]);
        output.Text.ShouldContain("market-query-marker");
        output.Text.ShouldNotContain("No marketplace items");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("info")]
    [InlineData("search")]
    public async Task ExecuteAsync_QueryCancelled_ReturnsCancellationWithoutEmptyResultMessage(string command)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Respond = path =>
        {
            if (path == "/health")
                return DataTransferFixture.Response(200);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        (await fixture.ExecuteAsync(command, cancellation.Token)).ShouldBe(130);

        fixture.Paths.ShouldBe(["/health", Endpoint(command)]);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_InfoItemNotFound_ReportsRequestedItem()
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceFixture();
        fixture.Respond = path => DataTransferFixture.Response(path == "/health" ? 200 : 404);

        (await fixture.ExecuteAsync("info", TestContext.Current.CancellationToken)).ShouldBe(1);

        output.Text.ShouldContain("Item 'item?marker' not found.");
        fixture.Paths.ShouldBe(["/health", Endpoint("info")]);
    }

    private static string Endpoint(string command) => command switch
    {
        "list" => "/api/marketplace",
        "info" => "/api/marketplace/item%3Fmarker",
        "search" => "/api/marketplace/search?q=query%20%26%20marker",
        _ => throw new ArgumentException("Unknown command.", nameof(command))
    };

    private sealed class MarketplaceFixture : HttpMessageHandler
    {
        private readonly HttpClient _client;
        private readonly GetSystemInfoAction _systemInfo;
        public List<string> Paths { get; } = [];
        public Func<string, HttpResponseMessage> Respond { get; set; } =
            _ => throw new InvalidOperationException("Unexpected marketplace request.");

        public MarketplaceFixture()
        {
            _client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://market.test") };
            var config = Substitute.For<ISystemConfigSource>();
            config.Load().Returns(new SystemConfigSnapshot
            {
                Version = "1.0",
                BaseUrl = "https://market.test",
                DefaultPort = 9401,
                Storage = "memory",
                AuthMode = "none",
                RequireHttps = true,
                WeaveHome = "unused",
                SiloPath = null
            });
            _systemInfo = new GetSystemInfoAction(config, _client);
        }

        public Task<int> ExecuteAsync(string command, CancellationToken ct) => command switch
        {
            "list" => new MarketplaceListCliCommand(_systemInfo, new BrowseMarketplaceItemsAction(_client))
                .ExecuteAsync(new NoCliOptions(), ct),
            "info" => new MarketplaceInfoCliCommand(_systemInfo, new BrowseMarketplaceItemsAction(_client), new GetMarketplaceItemAction(_client))
                .ExecuteAsync(new MarketplaceInfoOptions("item?marker"), ct),
            "search" => new MarketplaceSearchCliCommand(_systemInfo, new SearchMarketplaceAction(_client))
                .ExecuteAsync(new MarketplaceSearchOptions("query & marker"), ct),
            _ => throw new ArgumentException("Unknown command.", nameof(command))
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.ShouldBe(HttpMethod.Get);
            var path = request.RequestUri.ShouldNotBeNull().PathAndQuery;
            Paths.Add(path);
            return Task.FromResult(Respond(path));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _client.Dispose();
            base.Dispose(disposing);
        }
    }
}
