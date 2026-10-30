namespace Weave.Silo.Plugins;

public interface IInstallationDiagnostics
{
    InstallationObservation? Get(string installationId);
    void Record(string installationId, InstallationFailureCode failure);
    InstallationProbeObservation? GetProbe(Weave.Plugins.PluginInstallation installation);
    void RecordProbe(Weave.Plugins.PluginInstallation installation, InstallationFailureCode failure);
    void ClearProbe(string installationId);
}
