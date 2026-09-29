using Weave.Security.Tokens;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Plugins;

public sealed partial class PluginRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _requirements = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PluginStatus>> ConnectAllAsync(Dictionary<string, PluginDefinition> plugins, CapabilityToken token)
    {
        var remaining = new Dictionary<string, PluginDefinition>(plugins, StringComparer.Ordinal);
        var results = new Dictionary<string, PluginStatus>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(item => item.Value.Requires.Values.All(provider =>
                !remaining.Keys.Contains(provider, StringComparer.OrdinalIgnoreCase)))
                .Select(item => item.Key).ToArray();
            if (ready.Length == 0)
            {
                foreach (var (name, definition) in remaining)
                {
                    await _authorizer.AuthorizeAsync(token, $"plugin:invoke:{name}", actorWorkspaceId: null);
                    results[name] = new PluginStatus
                    {
                        Name = name,
                        Type = definition.Type,
                        IsConnected = false,
                        InstallationFailure = InstallationFailureCode.DependencyUnavailable,
                        Error = "Plugin service dependencies contain a cycle."
                    };
                }
                break;
            }

            foreach (var name in ready)
            {
                results[name] = await ConnectAsync(name, remaining[name], token);
                remaining.Remove(name);
            }
        }
        return plugins.Keys.Select(name => results[name]).ToList();
    }

    private PluginStatus? ValidateDependencies(string name, string type, IPluginConnector connector,
        IReadOnlyDictionary<string, string> requested, out Dictionary<string, string> requirements)
    {
        requirements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (service, providerName) in requested)
        {
            if (!requirements.TryAdd(service, providerName)
                || !connector.Schema.Consumes.Contains(service, StringComparer.OrdinalIgnoreCase))
                return DependencyInvalid(name, type,
                    $"Plugin type '{type}' does not consume service '{service}'.");

            if (string.IsNullOrWhiteSpace(providerName)
                || string.Equals(name, providerName, StringComparison.OrdinalIgnoreCase)
                || !_active.TryGetValue(providerName, out var provider)
                || !_connectorsByType.TryGetValue(provider.Type, out var providerConnector)
                || !providerConnector.Schema.Provides.Contains(service, StringComparer.OrdinalIgnoreCase)
                || !providerConnector.GetStatus(provider.Name).IsConnected)
                return DependencyInvalid(name, type,
                    $"Required service '{service}' from plugin '{providerName}' is unavailable.");
        }
        return null;
    }

    private string? FindDependent(string providerName) => _requirements.FirstOrDefault(item =>
        !string.Equals(item.Key, providerName, StringComparison.OrdinalIgnoreCase)
        && item.Value.Values.Contains(providerName, StringComparer.OrdinalIgnoreCase)).Key;

    private List<PluginStatus> GetDisposalOrder()
    {
        var pending = _active.Values.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        List<PluginStatus> order = [];
        while (pending.Count > 0)
        {
            var next = pending.Values.FirstOrDefault(candidate => !pending.Values.Any(other =>
                !string.Equals(other.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                && _requirements.TryGetValue(other.Name, out var required)
                && required.Values.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase)));
            if (next is null)
            {
                order.AddRange(pending.Values);
                break;
            }

            order.Add(next);
            pending.Remove(next.Name);
        }
        return order;
    }

    private static PluginStatus DependencyBlocked(string name, string type, string dependent) => new()
    {
        Name = name,
        Type = type,
        IsConnected = false,
        InstallationFailure = InstallationFailureCode.DependencyInUse,
        Error = $"Plugin '{name}' is required by active plugin '{dependent}'. Disconnect the dependent first."
    };

    private static PluginStatus DependencyInvalid(string name, string type, string error) => new()
    {
        Name = name,
        Type = type,
        IsConnected = false,
        InstallationFailure = InstallationFailureCode.DependencyUnavailable,
        Error = error
    };
}
