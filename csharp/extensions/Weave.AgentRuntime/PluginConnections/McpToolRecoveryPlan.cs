using System.Security.Cryptography;
using System.Text;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.ToolRegistry;

public sealed record McpToolRecoveryPlan
{
    public required string Name { get; init; }
    public string? PluginName { get; init; }
    public string? Url { get; init; }
    public bool Supported { get; init; }
    public string Digest { get; init; } = string.Empty;

    internal static McpToolRecoveryPlan From(string name, ToolDefinition definition)
    {
        var config = definition.Mcp;
        var supported = definition.Type == "mcp" && !string.IsNullOrWhiteSpace(definition.RequiresPlugin)
            && config is { Server: null, Args.Count: 0, Env.Count: 0 } && !string.IsNullOrWhiteSpace(config.Url)
            && config.AllowPrivateEndpoints && config.RequestTimeoutSeconds > 0 && config.IdleTimeoutSeconds > 0
            && config.MaxResponseBytes > 0 && config.MaxFrameBytes > 0 && config.MaxQueuedFrames > 0
            && definition.OpenApi is null && definition.Cli is null && definition.DirectHttp is null
            && definition.FileSystem is null && definition.Dapr is null;
        if (!supported)
            return new() { Name = name, PluginName = definition.RequiresPlugin, Supported = false };
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("weave-mcp-tool-recovery-v1");
            writer.Write(name);
            writer.Write(definition.Type);
            writer.Write(definition.Version ?? string.Empty);
            writer.Write(definition.RequiresPlugin!);
            writer.Write(config!.Url!);
            writer.Write(config.RequestTimeoutSeconds);
            writer.Write(config.IdleTimeoutSeconds);
            writer.Write(config.MaxResponseBytes);
            writer.Write(config.MaxFrameBytes);
            writer.Write(config.MaxQueuedFrames);
            writer.Write(config.AllowPrivateEndpoints);
        }
        return new()
        {
            Name = name,
            PluginName = definition.RequiresPlugin,
            Url = config.Url,
            Supported = true,
            Digest = Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()))
        };
    }
}
