using System.Security.Cryptography;
using System.Text.Json;
using Weave.Invocations;
using Weave.Tools.Tool;

namespace Weave.Tools.Connectors;

public sealed partial class FileSystemToolConnector : IApprovalTargetBinding
{
    public string? GetApprovalTargetDigest(ToolHandle handle)
    {
        if (handle.Type != ToolType.FileSystem || !handle.IsConnected
            || !_configurations.TryGetValue(handle.ConnectionId, out var config))
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // Revision changes when this adapter's approval-relevant dispatch semantics change.
            writer.WriteStartArray();
            writer.WriteStringValue("weave-filesystem-v1");
            writer.WriteStringValue(config.Root);
            writer.WriteBooleanValue(config.ReadOnly);
            writer.WriteNumberValue(config.MaxReadBytes);
            writer.WriteEndArray();
        }
        return "filesystem-v1:" + Convert.ToHexString(SHA256.HashData(
            buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
}
