using System.Collections.Concurrent;

namespace Weave.Silo.Plugins;

internal sealed class InstallationDiagnostics(TimeProvider timeProvider) : IInstallationDiagnostics
{
    private readonly ConcurrentDictionary<string, InstallationObservation> _observations =
        new(StringComparer.Ordinal);

    public InstallationObservation? Get(string installationId) =>
        _observations.TryGetValue(installationId, out var observation) ? observation : null;

    public void Record(string installationId, InstallationFailureCode failure) =>
        _observations[installationId] = new InstallationObservation(timeProvider.GetUtcNow(), failure);
}
