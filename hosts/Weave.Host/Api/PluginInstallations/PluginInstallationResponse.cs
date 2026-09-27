namespace Weave.Silo.Api;

public sealed record PluginInstallationResponse(
    string Id,
    string PluginName,
    string Type,
    bool DesiredEnabled,
    bool RuntimeConnected);
