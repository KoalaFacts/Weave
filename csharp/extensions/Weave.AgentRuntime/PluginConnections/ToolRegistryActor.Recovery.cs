using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Mapping;
using Weave.Tools.Tool;

namespace Weave.Agents.ToolRegistry;

public sealed partial class ToolRegistryActor
{
    public Task<IReadOnlyList<McpToolRecoveryPlan>> GetMcpRecoveryPlansAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureWorkspaceId();
        IReadOnlyList<McpToolRecoveryPlan> plans = persistentState.State.Definitions
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => McpToolRecoveryPlan.From(item.Key, item.Value)).ToArray();
        return Task.FromResult(plans);
    }

    public async Task<bool> RestoreMcpToolAsync(string toolName, string expectedDigest, CapabilityToken token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureWorkspaceId();
        if (!persistentState.State.Definitions.TryGetValue(toolName, out var definition))
            return false;
        var plan = McpToolRecoveryPlan.From(toolName, definition);
        if (!plan.Supported || plan.Digest != expectedDigest)
            return false;
        var tool = actors.GetActor<IToolActor>(VirtualActorId.From($"{_workspaceId}/{toolName}"));
        var handle = await tool.ConnectAsync(ToolSpecMapper.FromDefinition(toolName, definition) with
        {
            InstallationId = $"{_workspaceId}/{plan.PluginName}"
        }, token, ct);
        if (!handle.IsConnected || McpToolRecoveryPlan.From(toolName, persistentState.State.Definitions[toolName]).Digest != expectedDigest)
            return false;
        persistentState.State.Connections[toolName] = new()
        {
            ToolName = toolName,
            ToolType = definition.Type,
            Status = ToolConnectionStatus.Connected,
            ConnectedAt = timeProvider.GetUtcNow(),
            Endpoint = plan.Url
        };
        await persistentState.WriteStateAsync(ct);
        return true;
    }
}
