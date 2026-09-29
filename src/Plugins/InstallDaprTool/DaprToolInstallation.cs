using System.Security.Cryptography;
using System.Text;
using Weave.Plugins;

namespace Weave.Tools.InstallDaprTool;

public sealed record DaprToolInstallation : PluginInstallation
{
    // Bump when the Dapr tool schema or dispatch behavior changes.
    public const string ImplementationRevision = "dapr_tools/1";

    public int Port { get; set; }

    public static string ComputeConfigDigest(int port) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{ImplementationRevision}\n{port}")));
}
