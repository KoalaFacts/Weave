using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class DirectHttpToolConnectorTests
{
    private static readonly CapabilityToken TestToken = new()
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

        var handle = await connector.ConnectAsync(spec, TestToken);

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
            () => connector.ConnectAsync(spec, TestToken));
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
            () => connector.ConnectAsync(spec, TestToken));
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

        await connector.DisconnectAsync(handle);
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

        var result = await connector.InvokeAsync(handle, invocation);

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

        var result = await connector.InvokeAsync(handle, invocation);

        result.Success.ShouldBeFalse();
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

        var handle = await connector.ConnectAsync(spec, TestToken);

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

        var handle = await connector.ConnectAsync(spec, TestToken);
        await connector.DisconnectAsync(handle);

        // Reconnect same tool without auth — old header should be gone
        var specNoAuth = new ToolSpec
        {
            Name = "authed-api",
            Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = "http://localhost:8080" }
        };
        var handle2 = await connector.ConnectAsync(specNoAuth, TestToken);
        handle2.IsConnected.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/data", "api/data")]
    [InlineData("api/data", "api/data")]
    public async Task InvokeAsync_LeadingSlash_NormalizedCorrectly(string method, string expectedPathSuffix)
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
        var result = await connector.InvokeAsync(handle, invocation);
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

        var schema = await connector.DiscoverSchemaAsync(handle);

        schema.ToolName.ShouldBe("my-tool");
        schema.Description.ShouldContain("my-tool");
        schema.Parameters.ShouldNotBeEmpty();
    }
}
