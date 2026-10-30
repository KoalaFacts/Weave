using Weave.Shared.Plugins;

namespace Weave.Workspaces.Tests;

public sealed class PluginDisposalTests
{
    [Fact]
    public async Task DisposeIfSwappedAsync_Null_DoesNothing()
    {
        await PluginServiceBroker.DisposeIfSwappedAsync(null);
    }

    [Fact]
    public async Task DisposeIfSwappedAsync_IAsyncDisposable_CallsDisposeAsync()
    {
        var mock = new AsyncDisposableStub();

        await PluginServiceBroker.DisposeIfSwappedAsync(mock);

        mock.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeIfSwappedAsync_IDisposable_CallsDispose()
    {
        var mock = new DisposableStub();

        await PluginServiceBroker.DisposeIfSwappedAsync(mock);

        mock.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeIfSwappedAsync_BothInterfaces_PrefersAsync()
    {
        var mock = new DualDisposableStub();

        await PluginServiceBroker.DisposeIfSwappedAsync(mock);

        mock.AsyncDisposed.ShouldBeTrue();
        mock.SyncDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task DisposeIfSwappedAsync_PlainObject_DoesNothing()
    {
        await PluginServiceBroker.DisposeIfSwappedAsync("not disposable");
    }

    private sealed class AsyncDisposableStub : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }

    private sealed class DisposableStub : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class DualDisposableStub : IAsyncDisposable, IDisposable
    {
        public bool AsyncDisposed { get; private set; }
        public bool SyncDisposed { get; private set; }
        public ValueTask DisposeAsync() { AsyncDisposed = true; return default; }
        public void Dispose() => SyncDisposed = true;
    }
}
