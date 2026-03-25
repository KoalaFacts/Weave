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
    private readonly ConcurrentDictionary<string, object> _named = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Get the current override for <typeparamref name="T"/>, or null if none is set.
    /// </summary>
    public T? Get<T>() where T : class =>
        _services.TryGetValue(typeof(T), out var service) ? (T)service : null;

    /// <summary>
    /// Swap the implementation for <typeparamref name="T"/>. Returns the previous
    /// instance so the caller can dispose it.
    /// </summary>
    public T? Swap<T>(T? newService) where T : class
    {
        lock (_lock)
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
    }

    /// <summary>Store a named service instance.</summary>
    public void Set(string key, object service) => _named[key] = service;

    /// <summary>Retrieve a named service, or null.</summary>
    public T? Get<T>(string key) where T : class =>
        _named.TryGetValue(key, out var service) ? service as T : null;

    /// <summary>Remove a named service. Returns the removed instance for disposal.</summary>
    public object? Remove(string key) =>
        _named.TryRemove(key, out var service) ? service : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin service {ServiceType} swapped to {Implementation}")]
    private partial void LogServiceSwapped(string serviceType, string implementation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin service {ServiceType} cleared — reverting to default")]
    private partial void LogServiceCleared(string serviceType);
}
