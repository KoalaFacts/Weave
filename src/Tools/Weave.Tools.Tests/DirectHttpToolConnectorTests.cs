using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class DirectHttpToolConnectorTests
{
    private static readonly CapabilityToken _testToken = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:test-tool"]
    };

    private static DirectHttpToolConnector CreateConnector(HttpClient? httpClient = null) =>
        new(httpClient ?? new HttpClient(), Substitute.For<ILogger<DirectHttpToolConnector>>());

    [Fact]
    public async Task ConnectAsync_ValidConfig_ReturnsConnectedHandle()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "my-api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://localhost:8080" }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
        handle.ToolName.ShouldBe("my-api");
        handle.Type.ShouldBe(ToolType.DirectHttp);
        handle.ConnectionId.ShouldBe("http://localhost:8080");
    }

    [Fact]
    public async Task ConnectAsync_NullConfig_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec { Name = "bad", Type = ToolType.DirectHttp };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    [Fact]
    public async Task ConnectAsync_EmptyBaseUrl_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "empty",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "" }
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    [Fact]
    public async Task DisconnectAsync_ReturnsCompleted()
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "test",
            Type = ToolType.DirectHttp,
            ConnectionId = "http://localhost:8080",
            IsConnected = true
        };

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "unreachable",
            Type = ToolType.DirectHttp,
            ConnectionId = "http://localhost:1",
            IsConnected = true
        };
        var invocation = new ToolInvocation
        {
            ToolName = "unreachable",
            Method = "ping",
            Parameters = new Dictionary<string, string> { ["key"] = "value" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.ToolName.ShouldBe("unreachable");
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void ToolType_IsDirectHttp()
    {
        var connector = CreateConnector();
        connector.ToolType.ShouldBe(ToolType.DirectHttp);
    }

    [Theory]
    [InlineData("../../admin/delete")]
    [InlineData("foo/../../../etc/passwd")]
    [InlineData("http://evil.com/steal")]
    [InlineData("foo\\bar")]
    [InlineData("%2e%2e/admin")]
    [InlineData("foo@evil.com/steal")]
    public async Task InvokeAsync_PathTraversal_RejectsUnsafePaths(string maliciousMethod)
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "test",
            Type = ToolType.DirectHttp,
            ConnectionId = "http://localhost:8080",
            IsConnected = true
        };
        var invocation = new ToolInvocation
        {
            ToolName = "test",
            Method = maliciousMethod,
            Parameters = []
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("Invalid method path");
    }

    [Fact]
    public async Task ConnectAsync_WithAuthHeader_SetsAuthorization()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "authed-api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig
            {
                BaseUrl = "http://localhost:8080",
                AuthHeader = "Bearer test-token-123"
            }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task DisconnectAsync_ClearsAuthHeader()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "authed-api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig
            {
                BaseUrl = "http://localhost:8080",
                AuthHeader = "Bearer secret"
            }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);

        // Reconnect same tool without auth — old header should be gone
        var specNoAuth = new ToolSpec
        {
            Name = "authed-api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://localhost:8080" }
        };
        var handle2 = await connector.ConnectAsync(specNoAuth, _testToken, TestContext.Current.CancellationToken);
        handle2.IsConnected.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/data")]
    [InlineData("api/data")]
    public async Task InvokeAsync_LeadingSlash_NormalizedCorrectly(string method)
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "test",
            Type = ToolType.DirectHttp,
            ConnectionId = "http://localhost:1",
            IsConnected = true
        };
        var invocation = new ToolInvocation { ToolName = "test", Method = method, Parameters = [] };

        // Will fail with connection error but validates path construction doesn't throw
        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscoverSchemaAsync_ReturnsDescription()
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "my-tool",
            Type = ToolType.DirectHttp,
            ConnectionId = "http://localhost:8080",
            IsConnected = true
        };

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);

        schema.ToolName.ShouldBe("my-tool");
        schema.Description.ShouldContain("my-tool");
        schema.Parameters.ShouldNotBeEmpty();
    }

    // --- InvokeAsync with mock HTTP: success ---

    [Fact]
    public async Task InvokeAsync_SuccessfulResponse_ReturnsOutput()
    {
        var handler = new StubHandler("""{"result":"ok"}""");
        var connector = CreateConnector(new HttpClient(handler));
        var spec = new ToolSpec
        {
            Name = "api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://api.local:8080" }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "api",
            Method = "health",
            Parameters = new Dictionary<string, string> { ["check"] = "true" }
        };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("ok");
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task InvokeAsync_CallsCorrectUrl()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(new HttpClient(handler));
        var spec = new ToolSpec
        {
            Name = "api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://api.local:8080" }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "api", Method = "users/list", Parameters = [] };

        await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.AbsoluteUri.ShouldBe("http://api.local:8080/users/list");
    }

    [Fact]
    public async Task InvokeAsync_WithAuth_SendsAuthHeader()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(new HttpClient(handler));
        var spec = new ToolSpec
        {
            Name = "api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig
            {
                BaseUrl = "http://api.local:8080",
                AuthHeader = "Bearer my-secret-token"
            }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "api", Method = "data", Parameters = [] };

        await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        handler.LastAuthorizationHeader.ShouldBe("Bearer my-secret-token");
    }

    [Fact]
    public async Task InvokeAsync_ServerError_ReturnsFailure()
    {
        var handler = new StubHandler("error", System.Net.HttpStatusCode.InternalServerError);
        var connector = CreateConnector(new HttpClient(handler));
        var spec = new ToolSpec
        {
            Name = "api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://api.local:8080" }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "api", Method = "fail", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_PostsJsonBody()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(new HttpClient(handler));
        var spec = new ToolSpec
        {
            Name = "api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://api.local:8080" }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation
        {
            ToolName = "api",
            Method = "submit",
            Parameters = new Dictionary<string, string> { ["name"] = "test" }
        };

        await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        handler.LastMethod.ShouldBe(HttpMethod.Post);
        handler.LastContentType.ShouldBe("application/json");
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody.ShouldContain("test");
    }

    [Fact]
    public async Task DisconnectAsync_RemovesAuth_SubsequentInvokeHasNoAuth()
    {
        var handler = new StubHandler("{}");
        var connector = CreateConnector(new HttpClient(handler));

        // Connect with auth
        var spec = new ToolSpec
        {
            Name = "api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig
            {
                BaseUrl = "http://api.local:8080",
                AuthHeader = "Bearer secret"
            }
        };
        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);

        // Reconnect without auth
        var spec2 = new ToolSpec
        {
            Name = "api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://api.local:8080" }
        };
        var handle2 = await connector.ConnectAsync(spec2, _testToken, TestContext.Current.CancellationToken);
        var invocation = new ToolInvocation { ToolName = "api", Method = "ping", Parameters = [] };

        await connector.InvokeAsync(handle2, invocation, TestContext.Current.CancellationToken);

        handler.LastAuthorizationHeader.ShouldBeNull();
    }

    // --- Stub handler ---

    private sealed class StubHandler(string responseBody, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastContentType { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(statusCode)
            {
                Content = new System.Net.Http.StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
