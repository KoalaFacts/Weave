namespace Weave.Silo.Plugins;

internal sealed class PluginActivationStore
{
    private readonly Dictionary<string, PluginActivationScope> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public void Replace(string name, PluginActivationScope next)
    {
        lock (_lock)
        {
            next.Activate();
            _active.TryGetValue(name, out var previous);
            _active[name] = next;
            previous?.Dispose();
        }
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            if (!_active.Remove(name, out var scope))
                return false;

            scope.Dispose();
            return true;
        }
    }

    public bool Contains(string name)
    {
        lock (_lock)
            return _active.ContainsKey(name);
    }
}
