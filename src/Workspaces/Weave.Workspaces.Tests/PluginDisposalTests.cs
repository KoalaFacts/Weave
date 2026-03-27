using Weave.Workspaces.Plugins;

namespace Weave.Workspaces.Tests;

public sealed class PluginDisposalTests
{
    [Fact]
    public async Task DisposeIfNeededAsync_Null_DoesNothing()
    {
        await PluginDisposal.DisposeIfNeededAsync(null);
    }

    [Fact]
    public async Task DisposeIfNeededAsync_IAsyncDisposable_CallsDisposeAsync()
    {
        var mock = new AsyncDisposableStub();

        await PluginDisposal.DisposeIfNeededAsync(mock);

        mock.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeIfNeededAsync_IDisposable_CallsDispose()
    {
        var mock = new DisposableStub();

        await PluginDisposal.DisposeIfNeededAsync(mock);

        mock.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeIfNeededAsync_BothInterfaces_PrefersAsync()
    {
        var mock = new DualDisposableStub();

        await PluginDisposal.DisposeIfNeededAsync(mock);

        mock.AsyncDisposed.ShouldBeTrue();
        mock.SyncDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task DisposeIfNeededAsync_PlainObject_DoesNothing()
    {
        await PluginDisposal.DisposeIfNeededAsync("not disposable");
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
