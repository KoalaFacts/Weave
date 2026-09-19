using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Mapping;
using Weave.Tools.Marketplace;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Agents.ToolRegistry;

internal sealed class ToolRegistryConnector(
    IVirtualActorProvider actors,
    ICapabilityTokenService tokenService,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ToolSecretResolver secretResolver,
    ILogger logger,
    IActorState<ToolRegistryState> persistentState)
{
    public async Task ConnectAsync(string workspaceId, string toolName, ToolDefinition definition)
    {
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(workspaceId),
            ToolName = toolName,
            Phase = LifecyclePhase.ToolConnecting
        };

        persistentState.State.Connections[toolName] = new ToolConnection
        {
            ToolName = toolName,
            ToolType = definition.Type,
            Status = ToolConnectionStatus.Connecting,
            Endpoint = ToolSpecMapper.ResolveEndpoint(definition)
        };

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, CancellationToken.None);

        var resolvedDefinition = await secretResolver.ResolveAsync(workspaceId, definition);
        var toolSpec = ToolSpecMapper.FromDefinition(toolName, resolvedDefinition);
        using var source = tokenService.MintLinked(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = $"{workspaceId}/{toolName}",
            Grants = [$"tool:{toolName}"],
            Lifetime = TimeSpan.FromHours(1)
        }, CancellationToken.None);

        var toolActor = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/{toolName}"));
        await toolActor.ConnectAsync(toolSpec, source.Token);

        persistentState.State.Connections[toolName] = new ToolConnection
        {
            ToolName = toolName,
            ToolType = definition.Type,
            Status = ToolConnectionStatus.Connected,
            ConnectedAt = timeProvider.GetUtcNow(),
            Endpoint = ToolSpecMapper.ResolveEndpoint(resolvedDefinition)
        };

        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.ToolConnected,
            context with { Phase = LifecyclePhase.ToolConnected },
            CancellationToken.None);

        await eventBus.PublishAsync(new ToolConnectedEvent
        {
            SourceId = $"{workspaceId}/{toolName}",
            ToolName = toolName,
            WorkspaceId = WorkspaceId.From(workspaceId),
            ToolType = definition.Type
        }, CancellationToken.None);

        await persistentState.WriteStateAsync();
    }

    public async Task DisconnectAllAsync(string workspaceId)
    {
        foreach (var (toolName, connection) in persistentState.State.Connections.ToList()
            .Where(kvp => kvp.Value.Status is ToolConnectionStatus.Connected))
        {
            await DisconnectAsync(workspaceId, toolName, connection);
        }

        persistentState.State.Connections.Clear();
        persistentState.State.Definitions.Clear();
        persistentState.State.AgentToolAccess.Clear();
        await persistentState.WriteStateAsync();
    }

    private async Task DisconnectAsync(string workspaceId, string toolName, ToolConnection connection)
    {
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(workspaceId),
            ToolName = toolName,
            Phase = LifecyclePhase.ToolDisconnecting
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolDisconnecting, context, CancellationToken.None);

            var toolActor = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/{toolName}"));
            await toolActor.DisconnectAsync();

            persistentState.State.Connections[toolName] = connection with
            {
                Status = ToolConnectionStatus.Disconnected,
                ConnectedAt = null
            };

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.ToolDisconnected,
                context with { Phase = LifecyclePhase.ToolDisconnected },
                CancellationToken.None);

            await eventBus.PublishAsync(new ToolDisconnectedEvent
            {
                SourceId = $"{workspaceId}/{toolName}",
                ToolName = toolName,
                WorkspaceId = WorkspaceId.From(workspaceId)
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException)
        {
            logger.LogError(ex, "Failed to disconnect tool {ToolName}", toolName);
        }
    }
}
