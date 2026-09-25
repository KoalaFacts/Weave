using Microsoft.Extensions.Logging;

namespace Weave.Shared.Plugins;

/// <summary>
/// Holds mutable service slots that plugin connectors can swap at runtime.
/// </summary>
/// <remarks>
/// Registered as a singleton in DI. Proxy services (e.g., <see cref="EventBusProxy"/>)
/// delegate to the current backing instance from this broker, decoupling the frozen
/// DI container from dynamic plugin lifecycle. Typed services (<c>_services</c>) are
/// guarded by <c>_lock</c> for both reads and writes. Swap notifications run
/// in update order after releasing that lock.
/// Named services use a separate lock so removal checks object identity.
/// </remarks>
public sealed partial class PluginServiceBroker(ILogger<PluginServiceBroker> logger)
{
    private readonly Lock _lock = new();
    private readonly Lock _updateLock = new();
    private readonly Dictionary<Type, object> _services = [];
    private readonly Dictionary<Type, List<Action>> _swapCallbacks = [];
    private readonly Dictionary<string, object> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _namedLock = new();

    /// <summary>
    /// Get the current override for <typeparamref name="T"/>, or null if none is set.
    /// Reads under the same lock as <see cref="Swap{T}"/> for consistency.
    /// </summary>
    public T? Get<T>() where T : class
    {
        lock (_lock)
        {
            return _services.TryGetValue(typeof(T), out var service) ? (T)service : null;
        }
    }

    /// <summary>
    /// Swap the implementation for <typeparamref name="T"/>. Returns the previous
    /// instance. Serialized callbacks finish before the swap returns.
    /// </summary>
    public T? Swap<T>(T? newService) where T : class
    {
        lock (_updateLock)
        {
            T? previous = null;
            Action[] callbacks;
            lock (_lock)
            {
                if (newService is not null)
                {
                    _services.TryGetValue(typeof(T), out var existing);
                    previous = existing as T;
                    _services[typeof(T)] = newService;
                }
                else if (_services.Remove(typeof(T), out var removed))
                {
                    previous = (T)removed;
                }

                callbacks = SnapshotCallbacks(typeof(T));
            }

            if (newService is not null)
                LogServiceSwapped(typeof(T).Name, newService.GetType().Name);
            else
                LogServiceCleared(typeof(T).Name);

            foreach (var callback in callbacks)
                callback();

            return previous;
        }
    }

    public bool ClearIfCurrent<T>(T expected) where T : class
    {
        lock (_updateLock)
        {
            Action[] callbacks;
            lock (_lock)
            {
                if (!_services.TryGetValue(typeof(T), out var current) || !ReferenceEquals(current, expected))
                    return false;

                _services.Remove(typeof(T));
                callbacks = SnapshotCallbacks(typeof(T));
            }
            LogServiceCleared(typeof(T).Name);
            foreach (var callback in callbacks)
                callback();

            return true;
        }
    }

    public bool ReplaceIfCurrent<T>(T expected, T replacement) where T : class
    {
        lock (_updateLock)
        {
            Action[] callbacks;
            lock (_lock)
            {
                if (!_services.TryGetValue(typeof(T), out var current) || !ReferenceEquals(current, expected))
                    return false;

                _services[typeof(T)] = replacement;
                callbacks = SnapshotCallbacks(typeof(T));
            }
            LogServiceSwapped(typeof(T).Name, replacement.GetType().Name);
            foreach (var callback in callbacks)
                callback();

            return true;
        }
    }

    /// <summary>
    /// Register a callback that fires after a <see cref="Swap{T}"/> for <typeparamref name="T"/>.
    /// Multiple callbacks can be registered for the same type.
    /// </summary>
    public IDisposable OnSwap<T>(Action callback) where T : class
    {
        lock (_updateLock)
        {
            lock (_lock)
            {
                if (!_swapCallbacks.TryGetValue(typeof(T), out var list))
                {
                    list = [];
                    _swapCallbacks[typeof(T)] = list;
                }
                list.Add(callback);
            }
        }
        return new SwapCallbackRegistration(this, typeof(T), callback);
    }

    private Action[] SnapshotCallbacks(Type serviceType) =>
        _swapCallbacks.TryGetValue(serviceType, out var callbacks) ? [.. callbacks] : [];

    private sealed class SwapCallbackRegistration(PluginServiceBroker broker, Type serviceType, Action callback)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (broker._updateLock)
            {
                lock (broker._lock)
                {
                    if (!broker._swapCallbacks.TryGetValue(serviceType, out var callbacks))
                        return;

                    callbacks.Remove(callback);
                    if (callbacks.Count == 0)
                        broker._swapCallbacks.Remove(serviceType);
                }
            }
        }
    }

    /// <summary>Store a named service instance.</summary>
    public void Set(string key, object service)
    {
        lock (_namedLock)
            _named[key] = service;
    }

    /// <summary>Retrieve a named service, or null.</summary>
    public T? Get<T>(string key) where T : class
    {
        lock (_namedLock)
            return _named.TryGetValue(key, out var service) ? service as T : null;
    }

    /// <summary>Remove a named service. Returns the removed instance.</summary>
    public object? Remove(string key)
    {
        lock (_namedLock)
            return _named.Remove(key, out var service) ? service : null;
    }

    public bool RemoveIfCurrent(string key, object expected)
    {
        lock (_namedLock)
        {
            if (!_named.TryGetValue(key, out var current) || !ReferenceEquals(current, expected))
                return false;

            return _named.Remove(key);
        }
    }

    /// <summary>
    /// Dispose a swapped-out service instance if it implements
    /// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>.
    /// </summary>
    public static async ValueTask DisposeIfSwappedAsync(object? instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (instance is IDisposable disposable)
            disposable.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin service {ServiceType} swapped to {Implementation}")]
    private partial void LogServiceSwapped(string serviceType, string implementation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin service {ServiceType} cleared — reverting to default")]
    private partial void LogServiceCleared(string serviceType);
}
