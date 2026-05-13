using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Tests;

public sealed class OpenApiToolConnectorTests
{
    private const string SpecUrl = "http://api.example.test/openapi.json";

    private const string SampleSpec = """
    {
      "openapi": "3.0.0",
      "servers": [{"url": "http://api.example.test/v1"}],
      "paths": {
        "/items": {
          "get": {
            "operationId": "listItems",
            "summary": "List items",
            "parameters": [
              {"name": "limit", "in": "query", "required": false}
            ]
          },
          "post": {
            "operationId": "createItem",
            "summary": "Create item",
            "requestBody": {"required": true}
          }
        },
        "/items/{id}": {
          "get": {
            "operationId": "getItem",
            "parameters": [{"name": "id", "in": "path", "required": true}]
          },
          "delete": {
            "operationId": "deleteItem",
            "parameters": [{"name": "id", "in": "path", "required": true}]
          }
        },
        "/headers-echo": {
          "get": {
            "operationId": "echoHeader",
            "parameters": [{"name": "X-Trace-Id", "in": "header", "required": false}]
          }
        }
      }
    }
    """;

    private static readonly CapabilityToken _token = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:openapi-tool"]
    };

    private static ToolSpec NewSpec(string name = "my-api", AuthConfig? auth = null) => new()
    {
        Name = name,
        Type = ToolType.OpenApi,
        OpenApi = new OpenApiConfig { SpecUrl = SpecUrl, Auth = auth }
    };

    private static OpenApiToolConnector NewConnector(RouterHandler handler) =>
        new(new HttpClient(handler), NullLogger<OpenApiToolConnector>.Instance);

    [Fact]
    public async Task ConnectAsync_FetchesAndParsesSpec()
    {
        var handler = new RouterHandler().StubSpec(SpecUrl, SampleSpec);
        var connector = NewConnector(handler);

        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
        handle.Type.ShouldBe(ToolType.OpenApi);
        handler.SpecFetched.ShouldBeTrue();

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);
        schema.Description.ShouldContain("listItems");
        schema.Description.ShouldContain("getItem");
        schema.Parameters[0].Description.ShouldContain("listItems");
    }

    [Fact]
    public async Task ConnectAsync_NullOpenApiConfig_Throws()
    {
        var connector = NewConnector(new RouterHandler());
        var spec = new ToolSpec { Name = "bad", Type = ToolType.OpenApi };

        await Should.ThrowAsync<InvalidOperationException>(() => connector.ConnectAsync(spec, _token));
    }

    [Fact]
    public async Task ConnectAsync_SpecHasNoOperations_Throws()
    {
        var emptySpec = """{"openapi":"3.0.0","servers":[{"url":"http://x/"}],"paths":{}}""";
        var handler = new RouterHandler().StubSpec(SpecUrl, emptySpec);
        var connector = NewConnector(handler);

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectAsync_SpecMissingServers_FallsBackToSpecUrlAuthority()
    {
        var noServers = """{"openapi":"3.0.0","paths":{"/ping":{"get":{"operationId":"ping"}}}}""";
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, noServers)
            .StubResponse("GET", "http://api.example.test/ping", "pong");
        var connector = NewConnector(handler);

        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);
        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "ping" },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("pong");
    }

    [Fact]
    public async Task InvokeAsync_GetWithQueryParam_BuildsUrl()
    {
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, SampleSpec)
            .StubResponse("GET", "http://api.example.test/v1/items?limit=5", """{"items":[]}""");
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "listItems", Parameters = new() { ["limit"] = "5" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("""{"items":[]}""");
    }

    [Fact]
    public async Task InvokeAsync_PostWithRequestBody_SerializesParameters()
    {
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, SampleSpec)
            .StubResponse("POST", "http://api.example.test/v1/items", """{"id":42}""");
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "createItem", Parameters = new() { ["name"] = "Widget" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("""{"id":42}""");
        handler.LastRequestBody!.ShouldContain("Widget");
    }

    [Fact]
    public async Task InvokeAsync_PathParameter_SubstitutedAndEscaped()
    {
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, SampleSpec)
            .StubResponse("GET", "http://api.example.test/v1/items/abc%2Fdef", """{"id":"abc/def"}""");
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "getItem", Parameters = new() { ["id"] = "abc/def" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("abc/def");
    }

    [Fact]
    public async Task InvokeAsync_DeleteVerb_UsesCorrectMethod()
    {
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, SampleSpec)
            .StubResponse("DELETE", "http://api.example.test/v1/items/7", "");
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "deleteItem", Parameters = new() { ["id"] = "7" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.LastMethod.ShouldBe("DELETE");
    }

    [Fact]
    public async Task InvokeAsync_HeaderParameter_AttachedToRequest()
    {
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, SampleSpec)
            .StubResponse("GET", "http://api.example.test/v1/headers-echo", "ok");
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "echoHeader", Parameters = new() { ["X-Trace-Id"] = "trace-123" } },
            TestContext.Current.CancellationToken);

        handler.LastHeaders.ShouldContainKey("X-Trace-Id");
        handler.LastHeaders["X-Trace-Id"].ShouldBe("trace-123");
    }

    [Fact]
    public async Task InvokeAsync_BearerAuth_SetsAuthorizationHeader()
    {
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, SampleSpec)
            .StubResponse("GET", "http://api.example.test/v1/items", """{"items":[]}""");
        var connector = NewConnector(handler);
        var auth = new AuthConfig { Type = "bearer", Token = "my-secret" };
        var handle = await connector.ConnectAsync(NewSpec(auth: auth), _token, TestContext.Current.CancellationToken);

        await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "listItems" },
            TestContext.Current.CancellationToken);

        handler.LastAuthorizationHeader.ShouldBe("Bearer my-secret");
    }

    [Fact]
    public async Task InvokeAsync_UnknownOperation_ReturnsFailureWithMenu()
    {
        var handler = new RouterHandler().StubSpec(SpecUrl, SampleSpec);
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "doesNotExist" },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("doesNotExist");
        result.Error!.ShouldContain("listItems");
    }

    [Fact]
    public async Task InvokeAsync_MissingRequiredPathParameter_ReturnsFailure()
    {
        var handler = new RouterHandler().StubSpec(SpecUrl, SampleSpec);
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "getItem" },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("path parameter 'id'");
    }

    [Fact]
    public async Task InvokeAsync_NotConnected_ReturnsFailure()
    {
        var connector = NewConnector(new RouterHandler());
        var handle = new ToolHandle { ToolName = "x", Type = ToolType.OpenApi, ConnectionId = "openapi:unknown", IsConnected = true };

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "x", Method = "anything" },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not connected");
    }

    [Fact]
    public async Task InvokeAsync_RemoteReturnsError_SurfacesStatusAndBody()
    {
        var handler = new RouterHandler()
            .StubSpec(SpecUrl, SampleSpec)
            .StubResponse("GET", "http://api.example.test/v1/items", "rate limited", HttpStatusCode.TooManyRequests);
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "listItems" },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("429");
        result.Error!.ShouldContain("rate limited");
    }

    [Fact]
    public async Task DisconnectAsync_RemovesConnection()
    {
        var handler = new RouterHandler().StubSpec(SpecUrl, SampleSpec);
        var connector = NewConnector(handler);
        var handle = await connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "my-api", Method = "listItems" },
            TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not connected");
    }

    [Fact]
    public void ToolType_IsOpenApi()
    {
        NewConnector(new RouterHandler()).ToolType.ShouldBe(ToolType.OpenApi);
    }

    private sealed class RouterHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _routes = new(StringComparer.Ordinal);
        private string? _specUrl;
        private string? _specBody;

        public bool SpecFetched { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.Ordinal);

        public RouterHandler StubSpec(string url, string body)
        {
            _specUrl = url;
            _specBody = body;
            return this;
        }

        public RouterHandler StubResponse(string method, string url, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[$"{method} {url}"] = (body, status);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            LastMethod = request.Method.Method;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastHeaders.Clear();
            foreach (var header in request.Headers)
                LastHeaders[header.Key] = string.Join(",", header.Value);

            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);

            if (_specUrl is not null && url == _specUrl && request.Method == HttpMethod.Get)
            {
                SpecFetched = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_specBody!, Encoding.UTF8, "application/json")
                };
            }

            var key = $"{request.Method.Method} {url}";
            if (_routes.TryGetValue(key, out var route))
            {
                return new HttpResponseMessage(route.Status)
                {
                    Content = new StringContent(route.Body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"no route for {key}", Encoding.UTF8, "text/plain")
            };
        }
    }
}
