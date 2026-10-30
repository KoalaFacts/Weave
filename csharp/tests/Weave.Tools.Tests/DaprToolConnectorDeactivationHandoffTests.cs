using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class DaprToolConnectorDeactivationHandoffTests
{
    [Fact]
    public async Task Deactivate_AdmittedHttpRequestPending_RejectsNewWorkAndWaitsForItsRelease()
    {
        using var handler = new PendingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3500") };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connector = new DaprToolConnector(client, NullLogger<DaprToolConnector>.Instance);
        var spec = new ToolSpec
        {
            Name = "pending-tool",
            Type = ToolType.Dapr,
            Dapr = new DaprToolConfig { AppId = "owned-service", MethodName = "read" }
        };
        var token = new CapabilityToken
        {
            TokenId = "fake-handoff-token",
            WorkspaceId = "owned-workspace",
            Grants = ["tool:pending-tool:invoke:read"]
        };
        var handle = await connector.ConnectAsync(spec, token, cancellation.Token);
        var input = new ToolInvocation
        {
            ToolName = "pending-tool",
            Method = "read",
            Parameters = new Dictionary<string, string> { ["key"] = "owned" }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? worker = null;
        Task<ToolResult>? rejectedInvocation = null;
        var pending = connector.InvokeAsync(handle, input, cancellation.Token);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            pending.IsCompleted.ShouldBeFalse();
            worker = new Thread(() =>
            {
                entered.TrySetResult();
                var elapsed = Stopwatch.StartNew();
                try
                {
                    connector.Deactivate();
                    elapsed.Stop();
                    completed.TrySetResult(elapsed.Elapsed);
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
            })
            { IsBackground = true, Name = "Dapr pending handoff deactivation" };
            worker.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Entry alone is not proof that Deactivate changed state. Observe the public
            // connection refusal before asserting it remains blocked on the admitted request.
            await WaitUntilInactiveAsync(connector, spec, token, TestContext.Current.CancellationToken);
            pending.IsCompleted.ShouldBeFalse();
            completed.Task.IsCompleted.ShouldBeFalse("Deactivate must drain the already admitted HTTP request.");

            rejectedInvocation = connector.InvokeAsync(handle, input, cancellation.Token);
            var rejected = await rejectedInvocation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            rejected.Success.ShouldBeFalse();
            rejected.ToolName.ShouldBe("pending-tool");
            rejected.Error.ShouldBe("Dapr tool connector is inactive.");
            handler.RequestCount.ShouldBe(1);
            completed.Task.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                if (rejectedInvocation is not null)
                    await rejectedInvocation.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            finally
            {
                if (worker is not null)
                {
                    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                    worker.Join(TimeSpan.FromSeconds(5)).ShouldBeTrue("The completed deactivation worker must exit.");
                    (await completed.Task).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(5),
                        "The real deactivation call must finish within five seconds, including the pending-request handshake.");
                }
            }
        }

        var result = await pending;
        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("pending-tool");
        result.Error.ShouldNotBeNullOrWhiteSpace();
        result.Output.ShouldBeEmpty();
        handler.Cancelled.Task.IsCompletedSuccessfully.ShouldBeTrue();
        handler.RequestCount.ShouldBe(1);
        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, token, TestContext.Current.CancellationToken));
        refused.Message.ShouldBe("Dapr tool connector is inactive.");
    }

    private static async Task WaitUntilInactiveAsync(
        DaprToolConnector connector, ToolSpec spec, CapabilityToken token, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    try
                    {
                        // ConnectAsync may synchronously take the dispatch lock. Keep this
                        // public observation off the test caller so its outer wait stays bounded.
                        connector.ConnectAsync(spec, token, deadline.Token).GetAwaiter().GetResult();
                    }
                    catch (InvalidOperationException exception)
                    {
                        exception.Message.ShouldBe("Dapr tool connector is inactive.");
                        observed.TrySetResult();
                        return;
                    }
                    Thread.Yield();
                }
            }
            catch (Exception exception)
            {
                observed.TrySetException(exception);
            }
        })
        { IsBackground = true, Name = "Dapr public inactive observation" };
        observer.Start();
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            await deadline.CancelAsync();
            if (observed.Task.IsCompleted)
                observer.Join(TimeSpan.FromSeconds(5)).ShouldBeTrue("Completed public observation worker must exit.");
        }
    }

    private sealed class PendingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
