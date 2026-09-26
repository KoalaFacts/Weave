using System.Buffers.Binary;
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

    public static string ComputeConfigDigest(string url, string serverName, string serverVersion, string operation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(ImplementationRevision);
        Append(url);
        Append(serverName);
        Append(serverVersion);
        Append(operation);
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}
