using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Shared.Plugins;

namespace Weave.Shared.Tests;

public sealed class PluginServiceBrokerTests
{
    private readonly PluginServiceBroker _broker = new(NullLogger<PluginServiceBroker>.Instance);

    [Fact]
    public void Get_NoOverride_ReturnsNull()
    {
        _broker.Get<IEventBus>().ShouldBeNull();
    }

    [Fact]
    public void Swap_SetsAndReturnsNull_WhenNoPrevious()
    {
        var bus = Substitute.For<IEventBus>();

        var previous = _broker.Swap<IEventBus>(bus);

        previous.ShouldBeNull();
        _broker.Get<IEventBus>().ShouldBeSameAs(bus);
    }

    [Fact]
    public void Swap_ReturnsPreviousInstance()
    {
        var first = Substitute.For<IEventBus>();
        var second = Substitute.For<IEventBus>();

        _broker.Swap<IEventBus>(first);
        var previous = _broker.Swap<IEventBus>(second);

        previous.ShouldBeSameAs(first);
        _broker.Get<IEventBus>().ShouldBeSameAs(second);
    }

    [Fact]
    public void Swap_Null_ClearsOverride()
    {
        var bus = Substitute.For<IEventBus>();
        _broker.Swap<IEventBus>(bus);

        var previous = _broker.Swap<IEventBus>(null);

        previous.ShouldBeSameAs(bus);
        _broker.Get<IEventBus>().ShouldBeNull();
    }

    [Fact]
    public void OnSwap_CallbackFiredAfterSwap()
    {
        var callbackFired = false;
        _broker.OnSwap<IEventBus>(() => callbackFired = true);

        _broker.Swap<IEventBus>(Substitute.For<IEventBus>());

        callbackFired.ShouldBeTrue();
    }

    [Fact]
    public void OnSwap_CallbackFiredOnClear()
    {
        _broker.Swap<IEventBus>(Substitute.For<IEventBus>());
        var callbackFired = false;
        _broker.OnSwap<IEventBus>(() => callbackFired = true);

        _broker.Swap<IEventBus>(null);

        callbackFired.ShouldBeTrue();
    }

    [Fact]
    public void Named_SetAndGet_Works()
    {
        var client = new HttpClient();
        _broker.Set("http:my-api", client);

        _broker.Get<HttpClient>("http:my-api").ShouldBeSameAs(client);
    }

    [Fact]
    public void Named_Get_ReturnsNull_WhenMissing()
    {
        _broker.Get<HttpClient>("nonexistent").ShouldBeNull();
    }

    [Fact]
    public void Named_Remove_ReturnsPrevious()
    {
        var client = new HttpClient();
        _broker.Set("http:my-api", client);

        var removed = _broker.Remove("http:my-api");

        removed.ShouldBeSameAs(client);
        _broker.Get<HttpClient>("http:my-api").ShouldBeNull();
    }

    [Fact]
    public void Named_Remove_ReturnsNull_WhenMissing()
    {
        _broker.Remove("nonexistent").ShouldBeNull();
    }
}
