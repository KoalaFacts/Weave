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
        _fallback.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        await proxy.PublishAsync(new TestEvent("hello") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("hello");
    }

    [Fact]
    public async Task PublishAsync_WithOverride_DelegatesToOverride()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var mockBus = Substitute.For<IEventBus>();
        _broker.Swap<IEventBus>(mockBus);

        var evt = new TestEvent("routed") { SourceId = "test" };
        await proxy.PublishAsync(evt, CancellationToken.None);

        await mockBus.Received(1).PublishAsync(evt, CancellationToken.None);
    }

    [Fact]
    public async Task PublishAsync_AfterClear_RevertToFallback()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var mockBus = Substitute.For<IEventBus>();
        _broker.Swap<IEventBus>(mockBus);
        _broker.Swap<IEventBus>(null);

        TestEvent? received = null;
        _fallback.Subscribe<TestEvent>((e, _) => { received = e; return Task.CompletedTask; });

        await proxy.PublishAsync(new TestEvent("back") { SourceId = "test" }, CancellationToken.None);

        received.ShouldNotBeNull();
        received!.Data.ShouldBe("back");
    }

    [Fact]
    public void Subscribe_NoOverride_DelegatesToFallback()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var sub = proxy.Subscribe<TestEvent>((_, _) => Task.CompletedTask);

        sub.ShouldNotBeNull();
        sub.Dispose();
    }

    [Fact]
    public void Subscribe_WithOverride_DelegatesToOverride()
    {
        var proxy = new EventBusProxy(_broker, _fallback);
        var mockBus = Substitute.For<IEventBus>();
        _broker.Swap<IEventBus>(mockBus);

        Func<TestEvent, CancellationToken, Task> handler = (_, _) => Task.CompletedTask;
        proxy.Subscribe(handler);

        mockBus.Received(1).Subscribe(handler);
    }

    [Fact]
    public async Task HotSwap_MidFlight_NewPublishUsesNewBus()
    {
        var proxy = new EventBusProxy(_broker, _fallback);

        // Start with fallback
        TestEvent? fallbackReceived = null;
        _fallback.Subscribe<TestEvent>((e, _) => { fallbackReceived = e; return Task.CompletedTask; });
        await proxy.PublishAsync(new TestEvent("first") { SourceId = "test" }, CancellationToken.None);
        fallbackReceived.ShouldNotBeNull();

        // Swap to override
        var mockBus = Substitute.For<IEventBus>();
        _broker.Swap<IEventBus>(mockBus);

        var evt = new TestEvent("second") { SourceId = "test" };
        await proxy.PublishAsync(evt, CancellationToken.None);
        await mockBus.Received(1).PublishAsync(evt, CancellationToken.None);

        // Swap back
        _broker.Swap<IEventBus>(null);
        fallbackReceived = null;
        await proxy.PublishAsync(new TestEvent("third") { SourceId = "test" }, CancellationToken.None);
        fallbackReceived.ShouldNotBeNull();
        fallbackReceived!.Data.ShouldBe("third");
    }
}
