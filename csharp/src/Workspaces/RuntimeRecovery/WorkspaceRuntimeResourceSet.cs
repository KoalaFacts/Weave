using System.Security.Cryptography;
using System.Text;
using Weave.Workspaces.Lifecycle;

namespace Weave.Workspaces.RuntimeRecovery;

internal static class WorkspaceRuntimeResourceSet
{
    // BinaryWriter strings are length-prefixed; separators in names cannot alias another resource set.
    public static string Digest(WorkspaceState state)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("weave-runtime-resource-set-v1");
            writer.Write(state.WorkspaceId.ToString());
            writer.Write((int)state.Status);
            writer.Write((int)state.RecoveryCondition);
            writer.Write(state.RuntimeInstanceId.ToString("N"));
            writer.Write(state.RuntimeName ?? string.Empty);
            writer.Write(state.NetworkId?.ToString() ?? string.Empty);
            writer.Write(state.Containers.Count);
            foreach (var id in state.Containers.Select(item => item.ContainerId.ToString()).Order(StringComparer.Ordinal))
                writer.Write(id);
            WriteNames(state.ActiveAgents);
            WriteNames(state.ActiveTools);
            WriteNames(state.ActivePlugins);
            WriteNames(state.DaprToolInstallations.Where(item => item.DesiredEnabled).Select(item => item.Id));
            WriteNames(state.McpToolInstallations.Where(item => item.DesiredEnabled).Select(item => item.Id));

            void WriteNames(IEnumerable<string> names)
            {
                var ordered = names.Order(StringComparer.Ordinal).ToArray();
                writer.Write(ordered.Length);
                foreach (var name in ordered)
                    writer.Write(name);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
