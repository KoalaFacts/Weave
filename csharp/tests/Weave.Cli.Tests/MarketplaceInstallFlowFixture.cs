using System.Net;
using System.Text;
using Weave.Actions.Marketplace;
using Weave.Actions.SystemInfo;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

internal sealed class MarketplaceInstallFlowFixture : HttpMessageHandler
{
    private static readonly string[] ReadPaths = ["/health", "/api/marketplace"];
    public const string InstallPath = "/api/marketplace/item%3Fowned/install";
    public const string InstallJson = """
        {
          "item":{"itemId":"item?owned","name":"Review bundle [literal]","description":"A review bundle","category":"tools","version":"2.3.4","author":"fixture","status":"Published","installCount":17},
          "template":{"templateId":"template-owned","name":"Review template [literal]","description":"Review local documents.","version":"2.3.4","author":"fixture","status":"Published","tags":["review","local"],"instantiationCount":9,
            "agentDefinition":{"model":"fixture-model","provider":"fixture-provider","systemPromptFile":"remote-template.md","maxConcurrentTasks":3,"tools":["documents"],"capabilities":["tool:documents:invoke:read"]},
            "requiredTools":{"documents":{"type":"filesystem","version":"1.0"}}}
        }
        """;
    private readonly HttpClient _client;
    public List<string> Requests { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (request, _) => Task.FromResult(request.RequestUri.ShouldNotBeNull().AbsolutePath == "/health"
            ? Response(HttpStatusCode.OK)
            : Response(HttpStatusCode.OK, InstallJson));
    public MarketplaceInstallCliCommand Command { get; }

    public MarketplaceInstallFlowFixture(IWorkspaceRegistry registry)
    {
        _client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://market.test") };
        Command = new MarketplaceInstallCliCommand(registry,
            new GetSystemInfoAction(new FixedSystemConfig(), _client),
            new BrowseMarketplaceItemsAction(_client), new InstallMarketplaceItemAction(_client));
    }

    public void AssertInstallRequests() => Requests.ShouldBe(["GET /health", "POST " + InstallPath]);

    public static HttpResponseMessage Response(HttpStatusCode status, string json = "") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri.ShouldNotBeNull();
        var path = uri.PathAndQuery;
        uri.Host.ShouldBe("market.test");
        if (path == InstallPath)
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.Content.ShouldBeNull();
        }
        else
        {
            request.Method.ShouldBe(HttpMethod.Get);
            ReadPaths.ShouldContain(path);
        }
        Requests.Add(request.Method.Method + " " + path);
        return Respond(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _client.Dispose();
        base.Dispose(disposing);
    }

    internal sealed class UnusedRegistry : IWorkspaceRegistry
    {
        public void Register(string name, string absolutePath) => throw new InvalidOperationException("Failed or non-scaffolding installs must not register a workspace.");
        public void Unregister(string name) => throw new InvalidOperationException("Unexpected unregister.");
        public string? Resolve(string name) => throw new InvalidOperationException("Unexpected registry lookup.");
        public IReadOnlyDictionary<string, string> GetAll() => throw new InvalidOperationException("Unexpected registry enumeration.");
        public IEnumerable<string> GetNames() => throw new InvalidOperationException("Unexpected registry enumeration.");
    }

    private sealed class FixedSystemConfig : ISystemConfigSource
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
