using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class DaprToolConnectorLifecycleTests
{
    [Fact]
    public async Task ConnectAsync_WhitespaceAppId_RejectsConfiguration()
    {
        using var client = new HttpClient();
        var connector = new DaprToolConnector(client, NullLogger<DaprToolConnector>.Instance);
        var spec = new ToolSpec
        {
            Name = "order-processing",
            Type = ToolType.Dapr,
            Dapr = new DaprToolConfig { AppId = " \t " }
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, CreateToken(), TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("Tool 'order-processing' requires a Dapr app ID.");
    }

    [Fact]
    public async Task ConnectAsync_DeactivatedConnector_RejectsNewConnection()
    {
        using var client = new HttpClient();
        var connector = new DaprToolConnector(client, NullLogger<DaprToolConnector>.Instance);
        connector.Deactivate();

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(CreateSpec(), CreateToken(), TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("Dapr tool connector is inactive.");
    }

    [Fact]
    public async Task InvokeAsync_DeactivatedConnector_RejectsExistingHandleWithoutHttpHandoff()
    {
        using var handler = new LifecycleHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3500") };
        var connector = new DaprToolConnector(client, NullLogger<DaprToolConnector>.Instance);
        var handle = await connector.ConnectAsync(CreateSpec(), CreateToken(), TestContext.Current.CancellationToken);
        connector.Deactivate();

        var result = await connector.InvokeAsync(handle, CreateInvocation(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("order-processing");
        result.Error.ShouldBe("Dapr tool connector is inactive.");
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task InvokeAsync_DisposedHttpClient_ReleasesDispatchAdmissionBeforeRethrowing()
    {
        using var handler = new LifecycleHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3500") };
        var connector = new DaprToolConnector(client, NullLogger<DaprToolConnector>.Instance);
        var handle = await connector.ConnectAsync(CreateSpec(), CreateToken(), TestContext.Current.CancellationToken);
        client.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => connector.InvokeAsync(handle, CreateInvocation(), TestContext.Current.CancellationToken));
        await Task.Run(connector.Deactivate, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        handler.RequestCount.ShouldBe(0);
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(CreateSpec(), CreateToken(), TestContext.Current.CancellationToken));
        exception.Message.ShouldBe("Dapr tool connector is inactive.");
    }

    [Fact]
    public async Task InvokeAsync_CancelledHttpRequest_ReturnsFailureAndAllowsDeactivation()
    {
        using var handler = new LifecycleHandler(waitForCancellation: true);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3500") };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connector = new DaprToolConnector(client, NullLogger<DaprToolConnector>.Instance);
        var handle = await connector.ConnectAsync(CreateSpec(), CreateToken(), TestContext.Current.CancellationToken);

        var invocation = connector.InvokeAsync(handle, CreateInvocation(), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        invocation.IsCompleted.ShouldBeFalse();
        await cancellation.CancelAsync();
        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Run(connector.Deactivate, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("order-processing");
        result.Error.ShouldNotBeNullOrWhiteSpace();
        result.Output.ShouldBeEmpty();
        handler.RequestCount.ShouldBe(1);
        handler.RequestCancellation.IsCancellationRequested.ShouldBeTrue();
    }

    private static ToolSpec CreateSpec() => new()
    {
        Name = "order-processing",
        Type = ToolType.Dapr,
        Dapr = new DaprToolConfig { AppId = "orders-service", MethodName = "process" }
    };

    private static CapabilityToken CreateToken() => new()
    {
        TokenId = "dapr-lifecycle-test",
        WorkspaceId = "orders-workspace",
        Grants = ["tool:order-processing:invoke:process"]
    };

    private static ToolInvocation CreateInvocation() => new()
    {
        ToolName = "order-processing",
        Method = "process",
        Parameters = new Dictionary<string, string> { ["orderId"] = "order-1042" }
    };

    private sealed class LifecycleHandler(bool waitForCancellation = false) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }
        public CancellationToken RequestCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestCancellation = cancellationToken;
            Started.TrySetResult();
            if (waitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("order accepted") };
        }
    }
}
