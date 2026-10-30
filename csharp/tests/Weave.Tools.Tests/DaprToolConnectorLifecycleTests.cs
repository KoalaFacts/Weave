using System.Diagnostics;
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
        await DeactivateOnDedicatedThreadAsync(connector, TestContext.Current.CancellationToken);

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
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            invocation.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            await invocation.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        var result = await invocation;
        await DeactivateOnDedicatedThreadAsync(connector, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("order-processing");
        result.Error.ShouldNotBeNullOrWhiteSpace();
        result.Output.ShouldBeEmpty();
        handler.RequestCount.ShouldBe(1);
        handler.RequestCancellation.IsCancellationRequested.ShouldBeTrue();
    }

    private static async Task DeactivateOnDedicatedThreadAsync(DaprToolConnector connector, CancellationToken ct)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Deactivate can block in Monitor.Wait. Do not charge shared thread-pool queue delay
        // against its admission-release assertion, especially under coverage instrumentation.
        var worker = new Thread(() =>
        {
            entered.TrySetResult();
            try
            {
                var elapsed = Stopwatch.StartNew();
                connector.Deactivate();
                elapsed.Stop();
                completed.TrySetResult(elapsed.Elapsed);
            }
            catch (Exception exception)
            {
                // Surface worker failures on the test task instead of crashing the process.
                completed.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Dapr lifecycle deactivation assertion"
        };
        worker.Start();
        await WaitForPhaseAsync(entered.Task, "Dedicated deactivation worker did not start.", ct);
        await WaitForPhaseAsync(completed.Task, "Deactivate did not complete after the worker entered; dispatch admission may be retained.", ct);
        worker.Join(TimeSpan.FromSeconds(5)).ShouldBeTrue("Completed deactivation worker must exit.");
        (await completed.Task).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(5),
            "Deactivate itself must complete within five seconds, independent of test continuation scheduling.");
    }

    private static async Task WaitForPhaseAsync(Task phase, string timeoutMessage, CancellationToken ct)
    {
        try
        {
            await phase.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(timeoutMessage, exception);
        }
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
