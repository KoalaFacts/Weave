namespace Weave.Tools.InstallMcpTool;

public sealed record McpToolInstallationSnapshot
{
    public required string Id { get; init; }
    public required string PluginName { get; init; }
    public required string DefinitionRevision { get; init; }
    public required string ConfigDigest { get; init; }
    public required string Url { get; init; }
    public required string ServerName { get; init; }
    public required string ServerVersion { get; init; }
    public required string Operation { get; init; }
    public required string ContractDigest { get; init; }
    public bool HasUnsupportedAuthority { get; init; }

    public static McpToolInstallationSnapshot From(McpToolInstallation installation) => new()
    {
        Id = installation.Id,
        PluginName = installation.PluginName,
        DefinitionRevision = installation.DefinitionRevision,
        ConfigDigest = installation.ConfigDigest,
        Url = installation.Url,
        ServerName = installation.ServerName,
        ServerVersion = installation.ServerVersion,
        Operation = installation.Operation,
        ContractDigest = installation.ContractDigest,
        HasUnsupportedAuthority = installation.HasUnsupportedAuthority()
    };
}
