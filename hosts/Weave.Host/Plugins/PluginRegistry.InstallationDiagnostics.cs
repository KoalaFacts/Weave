namespace Weave.Silo.Plugins;

public sealed partial class PluginRegistry
{
    private PluginStatus ObserveInstallation(string name, PluginStatus status)
    {
        if (IsToolInstallation(name, status.Type))
            _installationDiagnostics.Record(name, status.IsConnected
                ? InstallationFailureCode.None
                : status.InstallationFailure ?? InstallationFailureCode.ConnectionFailed);
        return status;
    }

    private void ObserveDisconnected(string name, string type)
    {
        if (IsToolInstallation(name, type))
            _installationDiagnostics.Record(name, InstallationFailureCode.None);
    }

    private static bool IsToolInstallation(string name, string type) =>
        name.Contains('/', StringComparison.Ordinal)
        && (string.Equals(type, "mcp_tools", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "dapr_tools", StringComparison.OrdinalIgnoreCase));
}
