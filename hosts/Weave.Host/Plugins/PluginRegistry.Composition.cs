namespace Weave.Silo.Plugins;

public sealed partial class PluginRegistry
{
    public async Task<IReadOnlyList<PluginCompositionEntry>> GetCompositionAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            List<PluginCompositionEntry> entries = [];
            foreach (var status in _active.Values.OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase))
            {
                var connector = _connectorsByType[status.Type];
                var current = connector.GetStatus(status.Name);
                entries.Add(new PluginCompositionEntry
                {
                    Name = status.Name,
                    Type = status.Type,
                    Provides = [.. connector.Schema.Provides],
                    Registrations = [.. connector.RegistrationKeys(status.Name)],
                    IsConnected = current.IsConnected
                });
            }
            return entries;
        }
        finally
        {
            _connectLock.Release();
        }
    }
}
