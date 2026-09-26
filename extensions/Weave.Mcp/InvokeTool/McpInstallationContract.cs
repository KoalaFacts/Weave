using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

public sealed record McpInstallationContract(
    string Url,
    string ServerName,
    string ServerVersion,
    string Operation,
    string? ContractDigest)
{
    public McpConfig ProbeConfig() => new()
    {
        Url = Url,
        AllowPrivateEndpoints = true,
        RequestTimeoutSeconds = 10,
        IdleTimeoutSeconds = 10
    };
}
