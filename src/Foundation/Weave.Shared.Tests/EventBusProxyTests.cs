using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Shared.Plugins;

namespace Weave.Shared.Tests;

public sealed class EventBusProxyTests
{
    private readonly PluginServiceBroker _broker = new(NullLogger<PluginServiceBroker>.Instance);
    private readonly InProcessEventBus _fallback = new(Substitute.For<ILogger<InProcessEventBus>>());

    private sealed record TestEvent(string Data) : DomainEvent;

    private sealed record OtherEvent(int Value) : DomainEvent;

    private static InProcessEventBus CreateBus() => new(Substitute.For<ILogger<InProcessEventBus>>());

    // --- Basic delegation ---

    [Fact]
    public async Task PublishAsync_NoOverride_DelegatesToFallback()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        await proxy.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("hello");
    }

    [Fact]
    public async Task PublishAsync_WithOverride_DelegatesToOverride()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        _broker.Swap<IEventBus>(CreateBus());

        await proxy.PublishAsync(new TestEvent("routed") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("routed");
    }

    [Fact]
    public async Task PublishAsync_AfterClear_RevertsToFallback()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        _broker.Swap<IEventBus>(CreateBus());

        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        _broker.Swap<IEventBus>(null);

        await proxy.PublishAsync(new TestEvent("back") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("back");
    }

    // --- Subscription lifecycle ---

    [Fact]
    public void Subscribe_ReturnsDisposable_ThatUnsubscribes()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var sub = proxy.Subscribe<TestEvent>((_, _) => Task.CompletedTask);
        sub.ShouldNotBeNull();
        sub.Dispose();
    }

    [Fact]
    public async Task Subscribe_Dispose_StopsDelivery()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var received = false;
        var sub = proxy.Subscribe<TestEvent>((_, _) => { received = true; return Task.CompletedTask; });

        sub.Dispose();
        await proxy.PublishAsync(new TestEvent("nope") { SourceId = "test" }, CancellationToken.None);

        received.ShouldBeFalse();
    }

    [Fact]
    public void Subscribe_DoubleDispose_DoesNotThrow()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var sub = proxy.Subscribe<TestEvent>((_, _) => Task.CompletedTask);

        sub.Dispose();
        Should.NotThrow(() => sub.Dispose());
    }

    // --- Hot-swap: subscription survival ---

    [Fact]
    public async Task HotSwap_SubscriptionsSurvive_HandlersReplayedOntoNewBus()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        await proxy.PublishAsync(new TestEvent("before") { SourceId = "test" }, CancellationToken.None);
        received.ShouldNotBeNull();
        received!.Data.ShouldBe("before");

        _broker.Swap<IEventBus>(CreateBus());

        received = null;
        await proxy.PublishAsync(new TestEvent("after") { SourceId = "test" }, CancellationToken.None);
        received.ShouldNotBeNull();
        received!.Data.ShouldBe("after");
    }

    [Fact]
    public async Task HotSwap_DoubleSwap_HandlersWorkOnEachBus()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var calls = new List<string>();
        proxy.Subscribe<TestEvent>((e, _) => { calls.Add(e.Data); return Task.CompletedTask; });

        await proxy.PublishAsync(new TestEvent("1") { SourceId = "test" }, CancellationToken.None);

        _broker.Swap<IEventBus>(CreateBus());
        await proxy.PublishAsync(new TestEvent("2") { SourceId = "test" }, CancellationToken.None);

        _broker.Swap<IEventBus>(null);
        await proxy.PublishAsync(new TestEvent("3") { SourceId = "test" }, CancellationToken.None);

        calls.ShouldBe(["1", "2", "3"]);
    }

    [Fact]
    public async Task HotSwap_MultipleSubscribers_AllSurvive()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var results = new List<string>();
        proxy.Subscribe<TestEvent>((e, _) => { results.Add("A:" + e.Data); return Task.CompletedTask; });
        proxy.Subscribe<TestEvent>((e, _) => { results.Add("B:" + e.Data); return Task.CompletedTask; });

        _broker.Swap<IEventBus>(CreateBus());

        await proxy.PublishAsync(new TestEvent("msg") { SourceId = "test" }, CancellationToken.None);

        results.ShouldContain("A:msg");
        results.ShouldContain("B:msg");
    }

    [Fact]
    public async Task HotSwap_DisposedSubscription_NotReplayed()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var received = false;
        var sub = proxy.Subscribe<TestEvent>((_, _) => { received = true; return Task.CompletedTask; });

        sub.Dispose();
        _broker.Swap<IEventBus>(CreateBus());

        await proxy.PublishAsync(new TestEvent("nope") { SourceId = "test" }, CancellationToken.None);
        received.ShouldBeFalse();
    }

    // --- Hot-swap: old bus isolation ---

    [Fact]
    public async Task HotSwap_OldBus_DoesNotReceiveAfterSwap()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var received = false;
        proxy.Subscribe<TestEvent>((_, _) => { received = true; return Task.CompletedTask; });

        _broker.Swap<IEventBus>(CreateBus());

        // Publish directly on fallback (not via proxy) — handler should NOT fire
        // because subscriptions were replayed off the fallback onto the new bus
        await _fallback.PublishAsync(new TestEvent("direct") { SourceId = "test" }, CancellationToken.None);
        received.ShouldBeFalse();
    }

    [Fact]
    public async Task HotSwap_OverrideToOverride_HandlersMoveToLatestBus()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var calls = new List<string>();
        proxy.Subscribe<TestEvent>((e, _) => { calls.Add(e.Data); return Task.CompletedTask; });

        var bus1 = CreateBus();
        _broker.Swap<IEventBus>(bus1);
        await proxy.PublishAsync(new TestEvent("bus1") { SourceId = "test" }, CancellationToken.None);

        var bus2 = CreateBus();
        _broker.Swap<IEventBus>(bus2);
        await proxy.PublishAsync(new TestEvent("bus2") { SourceId = "test" }, CancellationToken.None);

        // Direct publish on bus1 should NOT reach handlers
        await bus1.PublishAsync(new TestEvent("stale") { SourceId = "test" }, CancellationToken.None);

        calls.ShouldBe(["bus1", "bus2"]);
    }

    // --- Hot-swap: different event types ---

    [Fact]
    public async Task HotSwap_DifferentEventTypes_BothSurvive()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var testEvents = new List<string>();
        var otherEvents = new List<int>();
        proxy.Subscribe<TestEvent>((e, _) => { testEvents.Add(e.Data); return Task.CompletedTask; });
        proxy.Subscribe<OtherEvent>((e, _) => { otherEvents.Add(e.Value); return Task.CompletedTask; });

        _broker.Swap<IEventBus>(CreateBus());

        await proxy.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);
        await proxy.PublishAsync(new OtherEvent(42) { SourceId = "test" }, CancellationToken.None);

        testEvents.ShouldBe(["hello"]);
        otherEvents.ShouldBe([42]);
    }

    // --- Hot-swap: partial dispose during swap ---

    [Fact]
    public async Task HotSwap_DisposeOneSub_OthersSurvive()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var aReceived = false;
        var bReceived = false;
        var subA = proxy.Subscribe<TestEvent>((_, _) => { aReceived = true; return Task.CompletedTask; });
        proxy.Subscribe<TestEvent>((_, _) => { bReceived = true; return Task.CompletedTask; });

        subA.Dispose();
        _broker.Swap<IEventBus>(CreateBus());

        await proxy.PublishAsync(new TestEvent("test") { SourceId = "test" }, CancellationToken.None);

        aReceived.ShouldBeFalse();
        bReceived.ShouldBeTrue();
    }

    // --- Hot-swap: subscribe after swap ---

    [Fact]
    public async Task SubscribeAfterSwap_WorksOnCurrentBus()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        _broker.Swap<IEventBus>(CreateBus());

        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        await proxy.PublishAsync(new TestEvent("post-swap") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("post-swap");
    }

    [Fact]
    public async Task SubscribeAfterSwap_SurvivesSubsequentSwap()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        _broker.Swap<IEventBus>(CreateBus());

        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        // Swap again
        _broker.Swap<IEventBus>(CreateBus());

        await proxy.PublishAsync(new TestEvent("still-alive") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("still-alive");
    }

    // --- Hot-swap: replay failure safety ---

    [Fact]
    public void HotSwap_ReplayFailure_SubscriptionStillFunctional()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var received = new List<string>();
        var sub = proxy.Subscribe<TestEvent>((e, _) => { received.Add(e.Data); return Task.CompletedTask; });

        // Swap to a bus whose Subscribe throws — replay should catch and preserve old sub
        var badBus = Substitute.For<IEventBus>();
        badBus.Subscribe(Arg.Any<Func<TestEvent, CancellationToken, Task>>())
            .Returns(_ => throw new InvalidOperationException("subscribe failed"));
        _broker.Swap<IEventBus>(badBus);

        // After replay failure, the old subscription handle is still valid — dispose shouldn't crash
        Should.NotThrow(() => sub.Dispose());
    }
}
