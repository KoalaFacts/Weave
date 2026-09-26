using System.Security.Cryptography;
using System.Text;

namespace Weave.Tools.InstallMcpTool;

public sealed record McpToolInstallation
{
    public const string ImplementationRevision = "mcp_tools/1";

    public string Id { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string ServerVersion { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string ContractDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
    public bool DesiredEnabled { get; set; }

    public static string ComputeConfigDigest(string url, string serverName, string serverVersion, string operation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ImplementationRevision}\n{url}\n{serverName}\n{serverVersion}\n{operation}")));
}
