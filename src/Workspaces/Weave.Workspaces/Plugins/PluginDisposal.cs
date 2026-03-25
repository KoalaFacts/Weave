namespace Weave.Workspaces.Plugins;

/// <summary>
/// Shared disposal helper for plugin services returned by <see cref="Shared.Plugins.PluginServiceBroker.Swap{T}"/>.
/// </summary>
public static class PluginDisposal
{
    public static async ValueTask DisposeIfNeededAsync(object? instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (instance is IDisposable disposable)
            disposable.Dispose();
    }
}
