using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Tools.Tests;

public sealed class OpenApiToolConnectorTests
{
    private static readonly CapabilityToken _testToken = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:openapi-tool"]
    };

    private static OpenApiToolConnector CreateConnector(HttpMessageHandler? handler = null) =>
        new(
            handler is not null ? new HttpClient(handler) : new HttpClient(),
            Substitute.For<ILogger<OpenApiToolConnector>>());

    private static ToolSpec CreateSpec(
        string name = "my-api",
        string specUrl = "http://localhost:8080/openapi.json",
        AuthConfig? auth = null) =>
        new()
        {
            Name = name,
            Type = ToolType.OpenApi,
            OpenApi = new OpenApiConfig { SpecUrl = specUrl, Auth = auth }
        };

    // --- ConnectAsync ---

    [Fact]
    public async Task ConnectAsync_ValidConfig_ReturnsConnectedHandle()
    {
        var connector = CreateConnector();

        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
        handle.ToolName.ShouldBe("my-api");
        handle.Type.ShouldBe(ToolType.OpenApi);
        handle.ConnectionId.ShouldBe("openapi:my-api");
    }

    [Fact]
    public async Task ConnectAsync_NullOpenApiConfig_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec { Name = "bad", Type = ToolType.OpenApi };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    [Fact]
    public async Task ConnectAsync_WithBearerAuth_SetsAuthorizationHeader()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(handler);
        var spec = CreateSpec(auth: new AuthConfig { Type = "bearer", Token = "my-token" });

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        handle.IsConnected.ShouldBeTrue();

        // Invoke to observe the auth header on the request
        var invocation = new ToolInvocation { ToolName = "my-api", Method = "test", Parameters = new() { ["endpoint"] = "/ping" } };
        await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        handler.LastAuthorizationHeader.ShouldBe("Bearer my-token");
    }

    // --- DisconnectAsync ---

    [Fact]
    public async Task DisconnectAsync_ReturnsCompleted()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    // --- DiscoverSchemaAsync ---

    [Fact]
    public async Task DiscoverSchemaAsync_ReturnsEndpointAndMethodParameters()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);

        schema.ToolName.ShouldBe("my-api");
        schema.Parameters.ShouldContain(p => p.Name == "endpoint" && p.Required);
        schema.Parameters.ShouldContain(p => p.Name == "http_method" && !p.Required);
    }

    [Fact]
    public void ToolType_IsOpenApi()
    {
        CreateConnector().ToolType.ShouldBe(ToolType.OpenApi);
    }

    // --- InvokeAsync: GET ---

    [Fact]
    public async Task InvokeAsync_DefaultMethod_UsesGet()
    {
        var handler = new StubHandler("""{"items":[]}""");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-api",
            Method = "list",
            Parameters = new() { ["endpoint"] = "/api/items" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.LastMethod.ShouldBe(HttpMethod.Get);
        handler.LastRequestUri!.PathAndQuery.ShouldBe("/api/items");
    }

    [Fact]
    public async Task InvokeAsync_ExplicitGet_UsesGet()
    {
        var handler = new StubHandler("[]");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-api",
            Method = "fetch",
            Parameters = new() { ["endpoint"] = "/data", ["http_method"] = "GET" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.LastMethod.ShouldBe(HttpMethod.Get);
    }

    // --- InvokeAsync: POST ---

    [Fact]
    public async Task InvokeAsync_PostMethod_SendsRawInputAsBody()
    {
        var handler = new StubHandler("""{"id":1}""");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-api",
            Method = "create",
            RawInput = """{"name":"test"}""",
            Parameters = new() { ["endpoint"] = "/api/items", ["http_method"] = "POST" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.LastMethod.ShouldBe(HttpMethod.Post);
        handler.LastRequestBody!.ShouldContain("test");
    }

    [Fact]
    public async Task InvokeAsync_PostWithNoRawInput_SendsEmptyJsonObject()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-api",
            Method = "submit",
            Parameters = new() { ["endpoint"] = "/submit", ["http_method"] = "POST" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.LastRequestBody.ShouldBe("{}");
    }

    // --- InvokeAsync: missing endpoint parameter ---

    [Fact]
    public async Task InvokeAsync_NoEndpointParam_DefaultsToRoot()
    {
        var handler = new StubHandler("ok");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-api", Method = "ping", Parameters = [] };

        await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        handler.LastRequestUri!.PathAndQuery.ShouldBe("/");
    }

    // --- InvokeAsync: errors ---

    [Fact]
    public async Task InvokeAsync_ServerError_ReturnsFailureWithStatusCode()
    {
        var handler = new StubHandler("bad request", HttpStatusCode.BadRequest);
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-api",
            Method = "invalid",
            Parameters = new() { ["endpoint"] = "/bad" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("400");
        result.Output.ShouldBe("bad request");
    }

    [Fact]
    public async Task InvokeAsync_NetworkFailure_ReturnsFailure()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-api",
            Method = "fail",
            Parameters = new() { ["endpoint"] = "/fail" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("connection refused");
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    // --- Stubs ---

    private sealed class StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
