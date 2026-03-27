using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Shared.Plugins;

namespace Weave.Shared.Tests;

public sealed class PluginServiceBrokerTests
{
    private readonly PluginServiceBroker _broker = new(NullLogger<PluginServiceBroker>.Instance);

    // --- Typed service swap ---

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

    // --- Swap callbacks ---

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
    public void OnSwap_MultipleCallbacks_AllFired()
    {
        var fired = new List<string>();
        _broker.OnSwap<IEventBus>(() => fired.Add("A"));
        _broker.OnSwap<IEventBus>(() => fired.Add("B"));

        _broker.Swap<IEventBus>(Substitute.For<IEventBus>());

        fired.ShouldBe(["A", "B"]);
    }

    [Fact]
    public void Swap_IndependentTypes_DontInterfere()
    {
        var bus = Substitute.For<IEventBus>();
        _broker.Swap<IEventBus>(bus);

        // Swapping a different type should not affect IEventBus
        _broker.Set("http:test", new HttpClient());

        _broker.Get<IEventBus>().ShouldBeSameAs(bus);
    }

    [Fact]
    public void Swap_CallbackSeesNewValue()
    {
        IEventBus? seenInCallback = null;
        _broker.OnSwap<IEventBus>(() => seenInCallback = _broker.Get<IEventBus>());
        var newBus = Substitute.For<IEventBus>();

        _broker.Swap<IEventBus>(newBus);

        seenInCallback.ShouldBeSameAs(newBus);
    }

    [Fact]
    public void Swap_ClearCallback_SeesNull()
    {
        _broker.Swap<IEventBus>(Substitute.For<IEventBus>());
        var callbackSawNull = false;
        _broker.OnSwap<IEventBus>(() => callbackSawNull = _broker.Get<IEventBus>() is null);

        _broker.Swap<IEventBus>(null);

        callbackSawNull.ShouldBeTrue();
    }

    // --- Named services ---

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

    [Fact]
    public void Named_Set_OverwritesPrevious()
    {
        var first = new HttpClient();
        var second = new HttpClient();
        _broker.Set("http:api", first);
        _broker.Set("http:api", second);

        _broker.Get<HttpClient>("http:api").ShouldBeSameAs(second);
    }
}
