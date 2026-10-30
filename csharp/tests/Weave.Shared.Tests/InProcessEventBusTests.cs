using Microsoft.Extensions.Logging;
using Weave.Shared.Events;

namespace Weave.Shared.Tests;

public sealed class InProcessEventBusTests
{
    private readonly InProcessEventBus _bus = new(Substitute.For<ILogger<InProcessEventBus>>());

    private sealed record TestEvent(string Data) : DomainEvent;

    private sealed record OtherEvent(int Value) : DomainEvent;

    [Fact]
    public async Task PublishAsync_DeliversToSubscriber()
    {
        TestEvent? received = null;
        _bus.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("hello");
    }

    [Fact]
    public async Task PublishAsync_WithNoSubscribers_CompletesSuccessfully()
    {
        // Should not throw
        await _bus.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);
    }

    [Fact]
    public async Task PublishAsync_MultipleSubscribers_AllReceive()
    {
        var received = new List<string>();
        _bus.Subscribe<TestEvent>((e, _) => { received.Add("sub1:" + e.Data); return Task.CompletedTask; });
        _bus.Subscribe<TestEvent>((e, _) => { received.Add("sub2:" + e.Data); return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("msg") { SourceId = "test" }, CancellationToken.None);

        received.Count.ShouldBe(2);
        received.ShouldContain("sub1:msg");
        received.ShouldContain("sub2:msg");
    }

    [Fact]
    public async Task Subscribe_ReturnsDisposable_ThatUnsubscribes()
    {
        var received = false;
        var subscription = _bus.Subscribe<TestEvent>((_, _) => { received = true; return Task.CompletedTask; });

        subscription.Dispose();

        await _bus.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);
        received.ShouldBeFalse();
    }

    [Fact]
    public async Task PublishAsync_HandlerException_DoesNotBlockOtherHandlers()
    {
        var secondHandlerCalled = false;
        _bus.Subscribe<TestEvent>((_, _) => throw new InvalidOperationException("boom"));
        _bus.Subscribe<TestEvent>((_, _) => { secondHandlerCalled = true; return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("test") { SourceId = "test" }, CancellationToken.None);

        secondHandlerCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_DifferentEventTypes_OnlyDeliverToMatchingSubscribers()
    {
        var testReceived = false;
        var otherReceived = false;
        _bus.Subscribe<TestEvent>((_, _) => { testReceived = true; return Task.CompletedTask; });
        _bus.Subscribe<OtherEvent>((_, _) => { otherReceived = true; return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);

        testReceived.ShouldBeTrue();
        otherReceived.ShouldBeFalse();
    }

    [Fact]
    public async Task PublishAsync_PassesCancellationToken()
    {
        CancellationToken receivedToken = default;
        using var cts = new CancellationTokenSource();
        _bus.Subscribe<TestEvent>((_, ct) => { receivedToken = ct; return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("hello") { SourceId = "test" }, cts.Token);

        receivedToken.ShouldBe(cts.Token);
    }

    // --- Subscription lifecycle edge cases ---

    [Fact]
    public void Subscribe_DoubleDispose_DoesNotThrow()
    {
        var sub = _bus.Subscribe<TestEvent>((_, _) => Task.CompletedTask);

        sub.Dispose();
        Should.NotThrow(() => sub.Dispose());
    }

    [Fact]
    public async Task Subscribe_DisposeOneOfMany_OthersStillReceive()
    {
        var received = new List<string>();
        var sub1 = _bus.Subscribe<TestEvent>((e, _) => { received.Add("A:" + e.Data); return Task.CompletedTask; });
        _bus.Subscribe<TestEvent>((e, _) => { received.Add("B:" + e.Data); return Task.CompletedTask; });

        sub1.Dispose();
        await _bus.PublishAsync(new TestEvent("msg") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotContain(s => s.StartsWith("A:"));
        received.ShouldContain("B:msg");
    }

    // --- Event ordering ---

    [Fact]
    public async Task PublishAsync_HandlersCalledInRegistrationOrder()
    {
        var order = new List<int>();
        _bus.Subscribe<TestEvent>((_, _) => { order.Add(1); return Task.CompletedTask; });
        _bus.Subscribe<TestEvent>((_, _) => { order.Add(2); return Task.CompletedTask; });
        _bus.Subscribe<TestEvent>((_, _) => { order.Add(3); return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("test") { SourceId = "test" }, CancellationToken.None);

        order.ShouldBe([1, 2, 3]);
    }

    // --- Multiple event types ---

    [Fact]
    public async Task PublishAsync_BothEventTypes_RoutedCorrectly()
    {
        var testEvents = new List<string>();
        var otherEvents = new List<int>();
        _bus.Subscribe<TestEvent>((e, _) => { testEvents.Add(e.Data); return Task.CompletedTask; });
        _bus.Subscribe<OtherEvent>((e, _) => { otherEvents.Add(e.Value); return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);
        await _bus.PublishAsync(new OtherEvent(42) { SourceId = "test" }, CancellationToken.None);
        await _bus.PublishAsync(new TestEvent("world") { SourceId = "test" }, CancellationToken.None);

        testEvents.ShouldBe(["hello", "world"]);
        otherEvents.ShouldBe([42]);
    }

    // --- Handler exception isolation ---

    [Fact]
    public async Task PublishAsync_AllHandlersThrow_DoesNotThrow()
    {
        _bus.Subscribe<TestEvent>((_, _) => throw new InvalidOperationException("boom1"));
        _bus.Subscribe<TestEvent>((_, _) => throw new InvalidOperationException("boom2"));

        // Should complete without throwing
        await _bus.PublishAsync(new TestEvent("test") { SourceId = "test" }, CancellationToken.None);
    }

    // --- Subscribe after publish ---

    [Fact]
    public async Task Subscribe_AfterPublish_OnlyReceivesFutureEvents()
    {
        await _bus.PublishAsync(new TestEvent("before") { SourceId = "test" }, CancellationToken.None);

        var received = new List<string>();
        _bus.Subscribe<TestEvent>((e, _) => { received.Add(e.Data); return Task.CompletedTask; });

        await _bus.PublishAsync(new TestEvent("after") { SourceId = "test" }, CancellationToken.None);

        received.ShouldBe(["after"]);
    }
}
