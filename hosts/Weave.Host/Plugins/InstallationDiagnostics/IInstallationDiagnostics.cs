namespace Weave.Silo.Plugins;

public interface IInstallationDiagnostics
{
    InstallationObservation? Get(string installationId);
    void Record(string installationId, InstallationFailureCode failure);
}
