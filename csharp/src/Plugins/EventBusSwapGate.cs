namespace Weave.Shared.Plugins;

internal sealed class EventBusSwapGate : IDisposable
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _readersLock = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _readers;

    public async Task<IDisposable> EnterReadAsync(CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken);
        try
        {
            await _readersLock.WaitAsync(cancellationToken);
            try
            {
                if (_readers == 0)
                    await _writer.WaitAsync(cancellationToken);
                _readers++;
            }
            finally
            {
                _readersLock.Release();
            }
        }
        finally
        {
            _turnstile.Release();
        }

        return new GateLease(ExitRead);
    }

    public IDisposable EnterWrite()
    {
        _turnstile.Wait();
        try
        {
            _writer.Wait();
            return new GateLease(ExitWrite);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }
    }

    private void ExitRead()
    {
        _readersLock.Wait();
        try
        {
            _readers--;
            if (_readers == 0)
                _writer.Release();
        }
        finally
        {
            _readersLock.Release();
        }
    }

    private void ExitWrite()
    {
        _writer.Release();
        _turnstile.Release();
    }

    public void Dispose()
    {
        _turnstile.Dispose();
        _readersLock.Dispose();
        _writer.Dispose();
    }

    private sealed class GateLease(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                release();
        }
    }
}
