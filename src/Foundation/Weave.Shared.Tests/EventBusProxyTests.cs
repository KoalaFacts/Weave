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
        var overrideBus = new InProcessEventBus(Substitute.For<ILogger<InProcessEventBus>>());
        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        _broker.Swap<IEventBus>(overrideBus);

        await proxy.PublishAsync(new TestEvent("routed") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("routed");
    }

    [Fact]
    public async Task PublishAsync_AfterClear_RevertsToFallback()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var overrideBus = new InProcessEventBus(Substitute.For<ILogger<InProcessEventBus>>());
        _broker.Swap<IEventBus>(overrideBus);

        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        _broker.Swap<IEventBus>(null);

        await proxy.PublishAsync(new TestEvent("back") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("back");
    }

    [Fact]
    public void Subscribe_ReturnsDisposable_ThatUnsubscribes()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var sub = proxy.Subscribe<TestEvent>((_, _) => Task.CompletedTask);
        sub.ShouldNotBeNull();
        sub.Dispose();
    }

    [Fact]
    public async Task Subscribe_SurvivesHotSwap_HandlersReplayedOntoNewBus()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        TestEvent? received = null;
        proxy.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        // Verify handler works on fallback
        await proxy.PublishAsync(new TestEvent("before") { SourceId = "test" }, CancellationToken.None);
        received.ShouldNotBeNull();
        received!.Data.ShouldBe("before");

        // Swap to new bus — handler should be replayed
        var overrideBus = new InProcessEventBus(Substitute.For<ILogger<InProcessEventBus>>());
        _broker.Swap<IEventBus>(overrideBus);

        received = null;
        await proxy.PublishAsync(new TestEvent("after") { SourceId = "test" }, CancellationToken.None);
        received.ShouldNotBeNull();
        received!.Data.ShouldBe("after");
    }

    [Fact]
    public async Task Subscribe_SurvivesDoubleSwap_HandlersWorkOnEachBus()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var calls = new List<string>();
        proxy.Subscribe<TestEvent>((e, _) => { calls.Add(e.Data); return Task.CompletedTask; });

        // fallback
        await proxy.PublishAsync(new TestEvent("1") { SourceId = "test" }, CancellationToken.None);

        // swap to override
        var bus2 = new InProcessEventBus(Substitute.For<ILogger<InProcessEventBus>>());
        _broker.Swap<IEventBus>(bus2);
        await proxy.PublishAsync(new TestEvent("2") { SourceId = "test" }, CancellationToken.None);

        // swap back to fallback
        _broker.Swap<IEventBus>(null);
        await proxy.PublishAsync(new TestEvent("3") { SourceId = "test" }, CancellationToken.None);

        calls.ShouldBe(["1", "2", "3"]);
    }

    [Fact]
    public async Task Dispose_Subscription_StopsDeliveryAfterSwap()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var received = false;
        var sub = proxy.Subscribe<TestEvent>((_, _) => { received = true; return Task.CompletedTask; });

        sub.Dispose();

        // Swap to new bus — disposed subscription should not be replayed
        var overrideBus = new InProcessEventBus(Substitute.For<ILogger<InProcessEventBus>>());
        _broker.Swap<IEventBus>(overrideBus);

        await proxy.PublishAsync(new TestEvent("nope") { SourceId = "test" }, CancellationToken.None);
        received.ShouldBeFalse();
    }

    [Fact]
    public async Task MultipleSubscribers_AllSurviveSwap()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var results = new List<string>();
        proxy.Subscribe<TestEvent>((e, _) => { results.Add("A:" + e.Data); return Task.CompletedTask; });
        proxy.Subscribe<TestEvent>((e, _) => { results.Add("B:" + e.Data); return Task.CompletedTask; });

        var overrideBus = new InProcessEventBus(Substitute.For<ILogger<InProcessEventBus>>());
        _broker.Swap<IEventBus>(overrideBus);

        await proxy.PublishAsync(new TestEvent("msg") { SourceId = "test" }, CancellationToken.None);

        results.ShouldContain("A:msg");
        results.ShouldContain("B:msg");
    }
}
