using Microsoft.Extensions.Logging;
using Orleans;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class ToolRegistryGrain(
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<ToolRegistryGrain> logger) : Grain, IToolRegistryGrain
{
    private readonly Dictionary<string, ToolConnection> _connections = [];
    private string _workspaceId = "unset";

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _workspaceId = this.GetPrimaryKeyString();
        return Task.CompletedTask;
    }

    public async Task ConnectToolsAsync(Dictionary<string, ToolDefinition> tools)
    {
        logger.LogInformation("Connecting {Count} tools for workspace {WorkspaceId}",
            tools.Count, _workspaceId);

        foreach (var (toolName, definition) in tools)
        {
            var context = new LifecycleContext
            {
                WorkspaceId = WorkspaceId.From(_workspaceId),
                ToolName = toolName,
                Phase = LifecyclePhase.ToolConnecting
            };

            try
            {
                await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, CancellationToken.None);

                var connection = new ToolConnection
                {
                    ToolName = toolName,
                    ToolType = definition.Type,
                    Status = ToolConnectionStatus.Connected,
                    ConnectedAt = DateTimeOffset.UtcNow,
                    Endpoint = ResolveEndpoint(definition)
                };

                _connections[toolName] = connection;

                await lifecycleManager.RunHooksAsync(
                    LifecyclePhase.ToolConnected,
                    context with { Phase = LifecyclePhase.ToolConnected },
                    CancellationToken.None);

                await eventBus.PublishAsync(new ToolConnectedEvent
                {
                    SourceId = $"{_workspaceId}/{toolName}",
                    ToolName = toolName,
                    WorkspaceId = WorkspaceId.From(_workspaceId),
                    ToolType = definition.Type
                }, CancellationToken.None);

                logger.LogInformation("Tool {ToolName} ({Type}) connected in workspace {WorkspaceId}",
                    toolName, definition.Type, _workspaceId);
            }
            catch (Exception ex)
            {
                _connections[toolName] = new ToolConnection
                {
                    ToolName = toolName,
                    ToolType = definition.Type,
                    Status = ToolConnectionStatus.Error,
                    ErrorMessage = ex.Message
                };

                await eventBus.PublishAsync(new ToolErrorEvent
                {
                    SourceId = $"{_workspaceId}/{toolName}",
                    ToolName = toolName,
                    WorkspaceId = WorkspaceId.From(_workspaceId),
                    ErrorMessage = ex.Message
                }, CancellationToken.None);

                logger.LogError(ex, "Failed to connect tool {ToolName}", toolName);
                throw;
            }
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var (toolName, connection) in _connections)
        {
            if (connection.Status is not ToolConnectionStatus.Connected)
                continue;

            var context = new LifecycleContext
            {
                WorkspaceId = WorkspaceId.From(_workspaceId),
                ToolName = toolName,
                Phase = LifecyclePhase.ToolDisconnecting
            };

            try
            {
                await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolDisconnecting, context, CancellationToken.None);

                _connections[toolName] = connection with
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
                    SourceId = $"{_workspaceId}/{toolName}",
                    ToolName = toolName,
                    WorkspaceId = WorkspaceId.From(_workspaceId)
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to disconnect tool {ToolName}", toolName);
            }
        }

        _connections.Clear();
    }

    public Task<ToolConnection?> GetConnectionAsync(string toolName)
    {
        _connections.TryGetValue(toolName, out var connection);
        return Task.FromResult(connection);
    }

    public Task<IReadOnlyList<ToolConnection>> GetAllConnectionsAsync()
    {
        IReadOnlyList<ToolConnection> result = _connections.Values.ToList();
        return Task.FromResult(result);
    }

    private static string? ResolveEndpoint(ToolDefinition definition) =>
        definition.Type switch
        {
            "mcp" => definition.Mcp?.Server,
            "openapi" => definition.OpenApi?.SpecUrl,
            _ => null
        };
}
