using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Shared.Events;
using Weave.Silo.Audit;

namespace Weave.Silo.Tests.Audit;

public sealed class CapabilityAuditSubscriberLifecycleTests
{
    [Fact]
    public async Task StopAsync_AfterDispose_CompletesWithoutAccessingDisposedSource()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var hosted = CreateSubscriber(bus, CreateStore());
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        hosted.Dispose();

        await hosted.StopAsync(TestContext.Current.CancellationToken);
        hosted.Dispose();
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_RecordsEachEventOnce()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var store = CreateStore();
        using var hosted = CreateSubscriber(bus, store);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        await bus.PublishAsync(Event(), TestContext.Current.CancellationToken);

        store.GetByToken("lifecycle-token").Count.ShouldBe(1);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        await bus.PublishAsync(Event(), TestContext.Current.CancellationToken);
        store.GetByToken("lifecycle-token").Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordWithRetry_CallbackCapturedBeforeShutdown_DoesNotWriteAfterShutdown(bool dispose)
    {
        var bus = Substitute.For<IEventBus>();
        Func<CapabilityAuthorizationEvent, CancellationToken, Task>? captured = null;
        bus.Subscribe(Arg.Do<Func<CapabilityAuthorizationEvent, CancellationToken, Task>>(handler => captured = handler))
            .Returns(Substitute.For<IDisposable>());
        var store = CreateStore();
        using var hosted = CreateSubscriber(bus, store);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        var handler = captured.ShouldNotBeNull();

        // Event buses can capture a subscriber before unsubscription, then invoke it later.
        if (dispose)
            hosted.Dispose();
        else
            await hosted.StopAsync(TestContext.Current.CancellationToken);

        await handler(Event(), TestContext.Current.CancellationToken);

        store.GetByToken("lifecycle-token").ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispose_WithoutStop_RemovesSubscriptionExactlyOnce()
    {
        var bus = Substitute.For<IEventBus>();
        var subscription = Substitute.For<IDisposable>();
        bus.Subscribe(Arg.Any<Func<CapabilityAuthorizationEvent, CancellationToken, Task>>())
            .Returns(subscription);
        using var hosted = CreateSubscriber(bus, CreateStore());
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        hosted.Dispose();
        hosted.Dispose();

        subscription.Received(1).Dispose();
        await hosted.StopAsync(TestContext.Current.CancellationToken);
        subscription.Received(1).Dispose();
    }

    [Fact]
    public async Task StartAsync_AfterDispose_RejectsResubscription()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var hosted = CreateSubscriber(bus, CreateStore());
        hosted.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => hosted.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_AfterStop_RejectsResubscription()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var hosted = CreateSubscriber(bus, CreateStore());
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => hosted.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAsync_ConcurrentDispose_CompletesWithoutDisposedSourceAccess()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        using var hosted = CreateSubscriber(bus, CreateStore());
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispose = Task.Run(async () =>
        {
            await release.Task;
            hosted.Dispose();
        }, TestContext.Current.CancellationToken);
        var stop = Task.Run(async () =>
        {
            await release.Task;
            await hosted.StopAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        release.SetResult();
        await Task.WhenAll(dispose, stop);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    private static InMemoryCapabilityAuditStore CreateStore() =>
        new(Options.Create(new CapabilityAuditOptions()));

    private static CapabilityAuditSubscriberHostedService CreateSubscriber(IEventBus bus, ICapabilityAuditStore store) =>
        new(bus, store, TimeProvider.System, NullLogger<CapabilityAuditSubscriberHostedService>.Instance);

    private static CapabilityAuthorizationEvent Event() => new()
    {
        SourceId = "ws/lifecycle-token",
        TokenId = "lifecycle-token",
        Grant = "tool:files:invoke:read_file",
        IssuedTo = "agent",
        WorkspaceId = "ws",
        ActionContext = "Test.Read",
        Outcome = CapabilityAuthorizationOutcome.Allow,
        Reason = null
    };
}
