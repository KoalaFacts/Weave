namespace Weave.Silo.Plugins;

public interface IMcpInstallationDispatchGate
{
    void BeginDisable(string installationId);
    bool MatchesInstallation(Weave.Tools.InstallMcpTool.McpToolInstallationSnapshot installation);
}
