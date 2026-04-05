using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Weave.Shared.Plugins;

/// <summary>
/// Holds mutable service slots that plugin connectors can swap at runtime.
/// Registered as a singleton in DI. Proxy services (e.g., <see cref="EventBusProxy"/>)
/// delegate to the current backing instance from this broker, decoupling the frozen
/// DI container from dynamic plugin lifecycle.
///
/// Typed services (_services) are guarded by _lock for both reads and writes —
/// Get and Swap are consistent. Named services (_named) use ConcurrentDictionary
/// since they have no callback mechanism.
/// </summary>
public sealed partial class PluginServiceBroker(ILogger<PluginServiceBroker> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Type, object> _services = [];
    private readonly Dictionary<Type, List<Action>> _swapCallbacks = [];
    private readonly ConcurrentDictionary<string, object> _named = new(StringComparer.OrdinalIgnoreCase);

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
    /// instance. Fires registered swap callbacks under the lock to ensure
    /// atomic swap+replay (no reader can observe the service before callbacks run).
    /// </summary>
    public T? Swap<T>(T? newService) where T : class
    {
        lock (_lock)
        {
            T? previous = null;
            if (newService is not null)
            {
                _services.TryGetValue(typeof(T), out var existing);
                previous = existing as T;
                _services[typeof(T)] = newService;
                LogServiceSwapped(typeof(T).Name, newService.GetType().Name);
            }
            else
            {
                if (_services.Remove(typeof(T), out var removed))
                    previous = (T)removed;
                LogServiceCleared(typeof(T).Name);
            }

            // Fire callbacks under the lock — snapshot the list to guard against
            // concurrent OnSwap registration from another thread.
            if (_swapCallbacks.TryGetValue(typeof(T), out var callbacks))
            {
                Action[] snapshot;
                lock (callbacks)
                { snapshot = [.. callbacks]; }
                foreach (var callback in snapshot)
                    callback();
            }

            return previous;
        }
    }

    /// <summary>
    /// Register a callback that fires after a <see cref="Swap{T}"/> for <typeparamref name="T"/>.
    /// Multiple callbacks can be registered for the same type.
    /// </summary>
    public void OnSwap<T>(Action callback) where T : class
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

    /// <summary>Store a named service instance.</summary>
    public void Set(string key, object service) => _named[key] = service;

    /// <summary>Retrieve a named service, or null.</summary>
    public T? Get<T>(string key) where T : class =>
        _named.TryGetValue(key, out var service) ? service as T : null;

    /// <summary>Remove a named service. Returns the removed instance.</summary>
    public object? Remove(string key) =>
        _named.TryRemove(key, out var service) ? service : null;

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
