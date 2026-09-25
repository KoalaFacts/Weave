namespace Weave.Silo.Plugins;

public sealed partial class PluginRegistry
{
    private int _disposed;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _connectLock.WaitAsync();
        List<Exception>? errors = null;
        try
        {
            foreach (var status in _active.Values)
            {
                try
                {
                    await _connectorsByType[status.Type].DisconnectAsync(status.Name);
                    _active.TryRemove(status.Name, out _);
                }
                catch (Exception error)
                {
                    (errors ??= []).Add(error);
                }
            }
        }
        finally
        {
            _connectLock.Release();
            _connectLock.Dispose();
        }

        if (errors is not null)
            throw new AggregateException("Plugin cleanup failed during host shutdown.", errors);
    }
}
