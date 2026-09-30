using System.Collections.Concurrent;
using Weave.Plugins;
using Weave.Tools.InstallMcpTool;

namespace Weave.Silo.Plugins;

internal sealed class InstallationDiagnostics(TimeProvider timeProvider) : IInstallationDiagnostics
{
    private readonly ConcurrentDictionary<string, InstallationObservation> _observations =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InstallationProbeObservation> _probes =
        new(StringComparer.Ordinal);

    public InstallationObservation? Get(string installationId) =>
        _observations.TryGetValue(installationId, out var observation) ? observation : null;

    public void Record(string installationId, InstallationFailureCode failure) =>
        _observations[installationId] = new InstallationObservation(timeProvider.GetUtcNow(), failure);

    public InstallationProbeObservation? GetProbe(PluginInstallation installation)
    {
        if (!_probes.TryGetValue(installation.Id, out var observation))
            return null;
        return string.Equals(observation.DefinitionRevision, installation.DefinitionRevision, StringComparison.Ordinal)
            && string.Equals(observation.ConfigDigest, installation.ConfigDigest, StringComparison.Ordinal)
            && string.Equals(observation.ContractDigest, ContractDigest(installation), StringComparison.Ordinal)
            ? observation : null;
    }

    public void RecordProbe(PluginInstallation installation, InstallationFailureCode failure) =>
        _probes[installation.Id] = new InstallationProbeObservation(timeProvider.GetUtcNow(), failure,
            installation.DefinitionRevision, installation.ConfigDigest, ContractDigest(installation));

    public void ClearProbe(string installationId) => _probes.TryRemove(installationId, out _);

    private static string ContractDigest(PluginInstallation installation) =>
        installation is McpToolInstallation mcp ? mcp.ContractDigest : string.Empty;
}
