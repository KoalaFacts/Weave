using System.Security.Cryptography;
using System.Text;

namespace Weave.Tools.InstallDaprTool;

public sealed record DaprToolInstallation
{
    // Bump when the Dapr tool schema or dispatch behavior changes.
    public const string ImplementationRevision = "dapr_tools/1";

    public string Id { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public int Port { get; set; }
    public string ConfigDigest { get; set; } = string.Empty;
    public bool DesiredEnabled { get; set; }

    public static string ComputeConfigDigest(int port) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{ImplementationRevision}\n{port}")));
}
