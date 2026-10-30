namespace Weave.Silo.Plugins;

internal sealed class PluginActivationScope : IDisposable
{
    private readonly List<(Action Apply, Action Revert)> _effects = [];
    private int _appliedCount;
    private bool _started;
    private bool _disposed;

    public void Add(Action apply, Action revert)
    {
        if (_started || _disposed)
            throw new InvalidOperationException("Plugin activation has already started.");

        _effects.Add((apply, revert));
    }

    public void Activate()
    {
        if (_started || _disposed)
            throw new InvalidOperationException("Plugin activation can start only once.");

        _started = true;
        try
        {
            foreach (var effect in _effects)
            {
                _appliedCount++;
                effect.Apply();
            }
        }
        catch (Exception activationError)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(activationError, cleanupError);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        List<Exception>? errors = null;
        for (var index = _appliedCount - 1; index >= 0; index--)
        {
            try
            {
                _effects[index].Revert();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        if (errors is not null)
            throw new AggregateException("Plugin activation cleanup failed.", errors);
    }
}
