using System.Net;
using System.Text;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

internal sealed class MarketplaceWriteFlowFixture : HttpMessageHandler
{
    private static readonly string[] WritePaths = ["/api/marketplace", "/api/marketplace/item%3Fowned/publish"];
    private readonly HttpClient _client;
    private readonly GetSystemInfoAction _system;
    public List<string> Requests { get; } = [];
    public string? Body { get; private set; }
    public Func<string, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => throw new InvalidOperationException("Response was not configured.");

    public MarketplaceWriteFlowFixture()
    {
        _client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://market.test") };
        _system = new GetSystemInfoAction(new FixedConfig(), _client);
    }

    public Task<int> ExecuteAsync(string operation, CancellationToken ct) => operation switch
    {
        "submit" => new MarketplaceSubmitCliCommand(_system, new SubmitMarketplaceItemAction(_client))
            .ExecuteAsync(new NoCliOptions(), ct),
        "publish" => new MarketplacePublishCliCommand(_system, new BrowseMarketplaceItemsAction(_client), new PublishMarketplaceItemAction(_client))
            .ExecuteAsync(new MarketplacePublishOptions("item?owned"), ct),
        _ => throw new ArgumentException("Unknown operation.", nameof(operation))
    };

    public static void QueueSubmission(MarketplacePromptConsole console, string tags = " local , , review ")
    {
        console.Line("Local review [literal]");
        console.Line("Review documents locally.");
        console.Key(ConsoleKey.DownArrow);
        console.Key(ConsoleKey.Enter, '\r');
        console.Line(""); // Accept the actual default version.
        console.Line("Fixture author");
        console.Line(tags);
    }

    public static void QueueReview(MarketplacePromptConsole console, bool approved, string notes)
    {
        console.Line("fixture-reviewer");
        console.Line(approved ? "y" : "n");
        console.Line(notes);
    }

    public void ConfigureResponse(HttpStatusCode status, string state = "Published") =>
        Respond = (path, _) => Task.FromResult(path == "/health" ? Response(HttpStatusCode.OK) : Response(status,
            "{\"itemId\":\"item?owned\",\"name\":\"Server item [literal]\",\"status\":\"" + state + "\"}"));

    public static HttpResponseMessage Response(HttpStatusCode status, string json = "") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri.ShouldNotBeNull();
        uri.Host.ShouldBe("market.test");
        var path = uri.PathAndQuery;
        if (path == "/health")
            request.Method.ShouldBe(HttpMethod.Get);
        else
        {
            WritePaths.ShouldContain(path);
            request.Method.ShouldBe(HttpMethod.Post);
            var content = request.Content.ShouldNotBeNull();
            content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/json");
            Body = await content.ReadAsStringAsync(cancellationToken);
        }
        Requests.Add(request.Method.Method + " " + path);
        return await Respond(path, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _client.Dispose();
        base.Dispose(disposing);
    }

    private sealed class FixedConfig : ISystemConfigSource
    {
        public SystemConfigSnapshot Load() => new()
        {
            Version = "test",
            BaseUrl = "https://market.test",
            DefaultPort = 9401,
            Storage = "memory",
            AuthMode = "none",
            RequireHttps = true,
            SiloPath = null,
            WeaveHome = "unused"
        };
    }
}
