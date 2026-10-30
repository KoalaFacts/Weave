using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class DirectHttpConnectionIsolationTests
{
    [Fact]
    public async Task InvokeAsync_RegisteredConnection_SendsItsEndpointAndCredential()
    {
        using var fx = new Fixture();
        var handle = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid/api", "Bearer fixture-a");

        var result = await fx.InvokeAsync(handle);

        result.Success.ShouldBeTrue(result.Error);
        var sent = fx.Requests.Single();
        sent.Url.ShouldBe("https://a.invalid/api/run");
        sent.Authorization.ShouldBe("Bearer fixture-a");
        sent.Body.ShouldBe("{\"value\":\"safe\"}");
    }

    [Theory]
    [InlineData("https://b.invalid/api")]
    [InlineData("https://a.invalid/api")]
    public async Task InvokeAsync_SameToolNameInTwoWorkspaces_KeepsEachCredential(string secondUrl)
    {
        using var fx = new Fixture();
        var first = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid/api", "Bearer fixture-a");
        var second = await fx.ConnectAsync("workspace-b", "shared", secondUrl, "Bearer fixture-b");

        (await fx.InvokeAsync(first)).Success.ShouldBeTrue();
        (await fx.InvokeAsync(second)).Success.ShouldBeTrue();

        var sent = fx.Requests.ToArray();
        sent.Length.ShouldBe(2);
        sent[0].Url.ShouldBe("https://a.invalid/api/run");
        sent[0].Authorization.ShouldBe("Bearer fixture-a");
        sent[1].Url.ShouldBe(secondUrl + "/run");
        sent[1].Authorization.ShouldBe("Bearer fixture-b");
    }

    [Fact]
    public async Task ConnectAsync_SameNameAndEndpoint_ReturnsIndependentHandles()
    {
        using var fx = new Fixture();
        var first = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid/api", "Bearer fixture-a");
        var second = await fx.ConnectAsync("workspace-b", "shared", "https://a.invalid/api", "Bearer fixture-b");

        first.ConnectionId.ShouldNotBe(second.ConnectionId);
        first.ConnectionId.ShouldNotBe("https://a.invalid/api");
        second.ConnectionId.ShouldNotBe("https://a.invalid/api");
    }

    [Fact]
    public async Task InvokeAsync_AnonymousConnectionAdded_DoesNotStripAnotherCredential()
    {
        using var fx = new Fixture();
        var authenticated = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid", "Bearer fixture-a");
        var anonymous = await fx.ConnectAsync("workspace-b", "shared", "https://b.invalid", null);

        (await fx.InvokeAsync(authenticated)).Success.ShouldBeTrue();
        (await fx.InvokeAsync(anonymous)).Success.ShouldBeTrue();

        var sent = fx.Requests.ToArray();
        sent[0].Authorization.ShouldBe("Bearer fixture-a");
        sent[1].Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedConnectionAdded_DoesNotGiveAnonymousConnectionItsCredential()
    {
        using var fx = new Fixture();
        var anonymous = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid", null);
        var authenticated = await fx.ConnectAsync("workspace-b", "shared", "https://b.invalid", "Bearer fixture-b");

        (await fx.InvokeAsync(anonymous)).Success.ShouldBeTrue();
        (await fx.InvokeAsync(authenticated)).Success.ShouldBeTrue();

        var sent = fx.Requests.ToArray();
        sent[0].Authorization.ShouldBeNull();
        sent[1].Authorization.ShouldBe("Bearer fixture-b");
    }

    [Fact]
    public async Task DisconnectAsync_OneConnection_DoesNotRemoveTheOtherCredential()
    {
        using var fx = new Fixture();
        var first = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid", "Bearer fixture-a");
        var second = await fx.ConnectAsync("workspace-b", "shared", "https://b.invalid", "Bearer fixture-b");

        await fx.Connector.DisconnectAsync(first, TestContext.Current.CancellationToken);
        (await fx.InvokeAsync(second)).Success.ShouldBeTrue();

        fx.Requests.Single().Authorization.ShouldBe("Bearer fixture-b");
    }

    [Fact]
    public async Task InvokeAsync_DisconnectedHandle_DoesNotSendAnAnonymousOrCrossAccountRequest()
    {
        using var fx = new Fixture();
        var stale = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid", "Bearer fixture-a");
        await fx.Connector.DisconnectAsync(stale, TestContext.Current.CancellationToken);
        await fx.ConnectAsync("workspace-b", "shared", "https://b.invalid", "Bearer fixture-b");

        var result = await fx.InvokeAsync(stale);

        result.Success.ShouldBeFalse();
        fx.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ReconnectSameWorkspaceAndName_DoesNotRetargetExistingHandle()
    {
        using var fx = new Fixture();
        var oldHandle = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid", "Bearer fixture-a");
        var newHandle = await fx.ConnectAsync("workspace-a", "shared", "https://b.invalid", "Bearer fixture-b");

        (await fx.InvokeAsync(oldHandle)).Success.ShouldBeTrue();
        (await fx.InvokeAsync(newHandle)).Success.ShouldBeTrue();

        var sent = fx.Requests.ToArray();
        sent[0].Url.ShouldBe("https://a.invalid/run");
        sent[0].Authorization.ShouldBe("Bearer fixture-a");
        sent[1].Url.ShouldBe("https://b.invalid/run");
        sent[1].Authorization.ShouldBe("Bearer fixture-b");
    }

    [Fact]
    public async Task InvokeAsync_CallerReplacesHandleIdWithUrl_DoesNotSendCredentialToThatUrl()
    {
        using var fx = new Fixture();
        var handle = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid", "Bearer fixture-a");

        var result = await fx.InvokeAsync(handle with { ConnectionId = "https://different.invalid/collect" });

        result.Success.ShouldBeFalse();
        fx.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("handle-name")]
    [InlineData("handle-type")]
    [InlineData("invocation-name")]
    [InlineData("disconnected-flag")]
    public async Task InvokeAsync_MismatchedConnectionIdentity_DoesNotSend(string mismatch)
    {
        using var fx = new Fixture();
        var handle = await fx.ConnectAsync("workspace-a", "shared", "https://a.invalid", "Bearer fixture-a");
        var invocation = Request(handle.ToolName);
        switch (mismatch)
        {
            case "handle-name":
                handle = handle with { ToolName = "other" };
                invocation = Request("other");
                break;
            case "handle-type":
                handle = handle with { Type = ToolType.Cli };
                break;
            case "invocation-name":
                invocation = Request("other");
                break;
            case "disconnected-flag":
                handle = handle with { IsConnected = false };
                break;
        }

        var result = await fx.Connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        fx.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_DifferentToolNames_RemainIndependent()
    {
        using var fx = new Fixture();
        var first = await fx.ConnectAsync("workspace-a", "one", "https://a.invalid", "Bearer fixture-a");
        var second = await fx.ConnectAsync("workspace-a", "two", "https://b.invalid", "Bearer fixture-b");

        (await fx.InvokeAsync(first)).Success.ShouldBeTrue();
        (await fx.InvokeAsync(second)).Success.ShouldBeTrue();

        var sent = fx.Requests.ToArray();
        sent[0].Authorization.ShouldBe("Bearer fixture-a");
        sent[1].Authorization.ShouldBe("Bearer fixture-b");
    }

    private static ToolInvocation Request(string name) => new()
    {
        ToolName = name,
        Method = "run",
        Parameters = new() { ["value"] = "safe" }
    };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        public ConcurrentQueue<(string Url, string? Authorization, string Body)> Requests { get; } = new();
        public DirectHttpToolConnector Connector { get; }

        public Fixture()
        {
            _client = new HttpClient(new RecordingHandler(Requests));
            Connector = new DirectHttpToolConnector(_client, NullLogger<DirectHttpToolConnector>.Instance);
        }

        public Task<ToolHandle> ConnectAsync(string workspace, string name, string url, string? authorization) =>
            Connector.ConnectAsync(new ToolSpec
            {
                Name = name,
                Type = ToolType.DirectHttp,
                DirectHttp = new DirectHttpToolConfig { BaseUrl = url, AuthHeader = authorization }
            }, new CapabilityToken { WorkspaceId = workspace, IssuedTo = workspace + "/agent" },
                TestContext.Current.CancellationToken);

        public Task<ToolResult> InvokeAsync(ToolHandle handle) =>
            Connector.InvokeAsync(handle, Request(handle.ToolName), TestContext.Current.CancellationToken);

        public void Dispose() => _client.Dispose();
    }

    private sealed class RecordingHandler(ConcurrentQueue<(string Url, string? Authorization, string Body)> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Enqueue((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
