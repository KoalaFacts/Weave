using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class DaprToolConnectorTests
{
    private static readonly CapabilityToken _testToken = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:dapr-tool"]
    };

    private static DaprToolConnector CreateConnector(HttpMessageHandler? handler = null) =>
        new(
            handler is not null ? new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3500") } : new HttpClient(),
            Substitute.For<ILogger<DaprToolConnector>>());

    private static ToolSpec CreateSpec(string name = "my-dapr", string appId = "order-service") =>
        new()
        {
            Name = name,
            Type = ToolType.Dapr,
            Dapr = new DaprToolConfig { AppId = appId, MethodName = "process" }
        };

    // --- ConnectAsync ---

    [Fact]
    public async Task ConnectAsync_ValidConfig_ReturnsConnectedHandle()
    {
        var connector = CreateConnector();

        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
        handle.ToolName.ShouldBe("my-dapr");
        handle.Type.ShouldBe(ToolType.Dapr);
        handle.ConnectionId.ShouldBe("dapr:order-service");
    }

    [Fact]
    public async Task ConnectAsync_NullDaprConfig_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec { Name = "bad", Type = ToolType.Dapr };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
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
    public async Task DiscoverSchemaAsync_ReturnsDescription()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);

        schema.ToolName.ShouldBe("my-dapr");
        schema.Description.ShouldContain("my-dapr");
    }

    [Fact]
    public void ToolType_IsDapr()
    {
        CreateConnector().ToolType.ShouldBe(ToolType.Dapr);
    }

    // --- InvokeAsync: success ---

    [Fact]
    public async Task InvokeAsync_SuccessfulResponse_ReturnsOutput()
    {
        var handler = new StubHandler("""{"result":"ok"}""");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-dapr",
            Method = "process",
            Parameters = new Dictionary<string, string> { ["orderId"] = "123" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("ok");
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task InvokeAsync_CallsCorrectDaprUrl()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(appId: "payments"), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-dapr",
            Method = "charge",
            Parameters = []
        };

        await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.PathAndQuery.ShouldBe("/v1.0/invoke/payments/method/charge");
    }

    [Fact]
    public async Task InvokeAsync_SendsPostWithJsonBody()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "my-dapr",
            Method = "test",
            Parameters = new Dictionary<string, string> { ["key"] = "value" }
        };

        await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        handler.LastMethod.ShouldBe(HttpMethod.Post);
        handler.LastContentType.ShouldBe("application/json");
    }

    // --- InvokeAsync: errors ---

    [Fact]
    public async Task InvokeAsync_ServerError_ReturnsFailure()
    {
        var handler = new StubHandler("internal error", HttpStatusCode.InternalServerError);
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-dapr", Method = "fail", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_NetworkFailure_ReturnsFailure()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var connector = CreateConnector(handler);
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "my-dapr", Method = "ping", Parameters = [] };

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
        public string? LastContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
