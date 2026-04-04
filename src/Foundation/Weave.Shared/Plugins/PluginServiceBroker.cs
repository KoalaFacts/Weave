using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Weave.Shared.Plugins;

/// <summary>
/// Holds mutable service slots that plugin connectors can swap at runtime.
/// Registered as a singleton in DI. Proxy services (e.g., <see cref="EventBusProxy"/>)
/// delegate to the current backing instance from this broker, decoupling the frozen
/// DI container from dynamic plugin lifecycle.
/// </summary>
public sealed partial class PluginServiceBroker(ILogger<PluginServiceBroker> logger)
{
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<Type, object> _services = new();
    private readonly ConcurrentDictionary<Type, List<Action>> _swapCallbacks = new();
    private readonly ConcurrentDictionary<string, object> _named = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Get the current override for <typeparamref name="T"/>, or null if none is set.
    /// </summary>
    public T? Get<T>() where T : class =>
        _services.TryGetValue(typeof(T), out var service) ? (T)service : null;

    /// <summary>
    /// Swap the implementation for <typeparamref name="T"/>. Returns the previous
    /// instance so the caller can dispose it. Fires registered swap callbacks
    /// sequentially under the lock to ensure atomic swap+replay.
    /// </summary>
    public T? Swap<T>(T? newService) where T : class
    {
        lock (_lock)
        {
            T? previous = SwapCore<T>(newService);

            // Fire callbacks under the lock to ensure no concurrent publish
            // can observe a state where the service is swapped but subscriptions
            // have not been replayed yet.
            if (_swapCallbacks.TryGetValue(typeof(T), out var callbacks))
            {
                // M6 fix: snapshot under the list's lock to avoid
                // InvalidOperationException if OnSwap is called concurrently.
                Action[] snapshot;
                lock (callbacks) { snapshot = [.. callbacks]; }
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
        var list = _swapCallbacks.GetOrAdd(typeof(T), _ => []);
        lock (list) { list.Add(callback); }
    }

    /// <summary>Store a named service instance.</summary>
    public void Set(string key, object service) => _named[key] = service;

    /// <summary>Retrieve a named service, or null.</summary>
    public T? Get<T>(string key) where T : class =>
        _named.TryGetValue(key, out var service) ? service as T : null;

    /// <summary>Remove a named service. Returns the removed instance for disposal.</summary>
    public object? Remove(string key) =>
        _named.TryRemove(key, out var service) ? service : null;

    private T? SwapCore<T>(T? newService) where T : class
    {
        T? previous = null;
        if (newService is not null)
        {
            if (_services.TryGetValue(typeof(T), out var existing))
                previous = (T)existing;
            _services[typeof(T)] = newService;
            LogServiceSwapped(typeof(T).Name, newService.GetType().Name);
        }
        else
        {
            if (_services.TryRemove(typeof(T), out var removed))
                previous = (T)removed;
            LogServiceCleared(typeof(T).Name);
        }
        return previous;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin service {ServiceType} swapped to {Implementation}")]
    private partial void LogServiceSwapped(string serviceType, string implementation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin service {ServiceType} cleared — reverting to default")]
    private partial void LogServiceCleared(string serviceType);

    /// <summary>
    /// Dispose a swapped-out service instance if it implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>.
    /// Prefers async disposal when both are implemented.
    /// </summary>
    public static async ValueTask DisposeIfSwappedAsync(object? instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (instance is IDisposable disposable)
            disposable.Dispose();
    }
}
