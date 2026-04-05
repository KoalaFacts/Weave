using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Security.Grains;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Grains;
using Weave.Tools.Mapping;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class ToolRegistryGrain(
    IGrainFactory grainFactory,
    ICapabilityTokenService tokenService,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<ToolRegistryGrain> logger,
    [PersistentState("tool-registry", "Default")] IPersistentState<ToolRegistryState> persistentState) : Grain, IToolRegistryGrain
{
    private string _workspaceId = "unset";

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await persistentState.ReadStateAsync(cancellationToken);
        EnsureWorkspaceId();
        if (!string.Equals(persistentState.State.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            persistentState.State.WorkspaceId = _workspaceId;
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task ConnectToolsAsync(Dictionary<string, ToolDefinition> tools)
    {
        EnsureWorkspaceId();
        logger.LogInformation("Connecting {Count} tools for workspace {WorkspaceId}", tools.Count, _workspaceId);

        foreach (var (toolName, definition) in tools)
        {
            persistentState.State.Definitions[toolName] = definition;
            await ConnectToolAsync(toolName, definition);
        }

        await persistentState.WriteStateAsync();
    }

    public async Task ConfigureAccessAsync(Dictionary<string, List<string>> agentToolAccess)
    {
        EnsureWorkspaceId();
        persistentState.State.AgentToolAccess.Clear();
        foreach (var (agentName, toolNames) in agentToolAccess)
        {
            persistentState.State.AgentToolAccess[agentName] = [.. toolNames.Distinct(StringComparer.Ordinal)];
        }

        await persistentState.WriteStateAsync();
    }

    public async Task GrantAgentToolsAsync(string agentName, IReadOnlyList<string> toolNames)
    {
        EnsureWorkspaceId();
        persistentState.State.AgentToolAccess[agentName] = [.. toolNames.Distinct(StringComparer.Ordinal)];
        await persistentState.WriteStateAsync();
    }

    public async Task DisconnectAllAsync()
    {
        EnsureWorkspaceId();
        foreach (var (toolName, connection) in persistentState.State.Connections.ToList())
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

                var toolGrain = grainFactory.GetGrain<IToolGrain>($"{_workspaceId}/{toolName}");
                await toolGrain.DisconnectAsync();

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

        persistentState.State.Connections.Clear();
        persistentState.State.Definitions.Clear();
        persistentState.State.AgentToolAccess.Clear();
        await persistentState.WriteStateAsync();
    }

    public Task<ToolConnection?> GetConnectionAsync(string toolName)
    {
        persistentState.State.Connections.TryGetValue(toolName, out var connection);
        return Task.FromResult(connection);
    }

    public Task<IReadOnlyList<ToolConnection>> GetAllConnectionsAsync()
    {
        IReadOnlyList<ToolConnection> result = [.. persistentState.State.Connections.Values];
        return Task.FromResult(result);
    }

    public async Task<ToolResolution?> ResolveAsync(string agentName, string toolName)
    {
        EnsureWorkspaceId();
        if (!IsToolAllowed(agentName, toolName))
            return null;

        if (!persistentState.State.Definitions.TryGetValue(toolName, out var definition))
            return null;

        if (!persistentState.State.Connections.TryGetValue(toolName, out var connection) ||
            connection.Status is not ToolConnectionStatus.Connected)
        {
            await ConnectToolAsync(toolName, definition);
            connection = persistentState.State.Connections[toolName];
        }

        var grainKey = $"{_workspaceId}/{toolName}";
        var toolGrain = grainFactory.GetGrain<IToolGrain>(grainKey);
        if (await toolGrain.GetHandleAsync() is null)
        {
            await ConnectToolAsync(toolName, definition);
        }

        var token = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = _workspaceId,
            IssuedTo = $"{_workspaceId}/{agentName}",
            Grants = [$"tool:{toolName}"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var schema = await toolGrain.GetSchemaAsync();
        return new ToolResolution
        {
            ToolName = toolName,
            GrainKey = grainKey,
            Token = token,
            Schema = schema
        };
    }

    private async Task ConnectToolAsync(string toolName, ToolDefinition definition)
    {
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(_workspaceId),
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

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, CancellationToken.None);

            var resolvedDefinition = await ResolveSecretsAsync(definition);
            var toolSpec = ToolSpecMapper.FromDefinition(toolName, resolvedDefinition);
            var token = tokenService.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = _workspaceId,
                IssuedTo = $"{_workspaceId}/{toolName}",
                Grants = [$"tool:{toolName}", "secret:*"],
                Lifetime = TimeSpan.FromHours(1)
            });

            var toolGrain = grainFactory.GetGrain<IToolGrain>($"{_workspaceId}/{toolName}");
            await toolGrain.ConnectAsync(toolSpec, token);

            persistentState.State.Connections[toolName] = new ToolConnection
            {
                ToolName = toolName,
                ToolType = definition.Type,
                Status = ToolConnectionStatus.Connected,
                ConnectedAt = DateTimeOffset.UtcNow,
                Endpoint = ToolSpecMapper.ResolveEndpoint(resolvedDefinition)
            };

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

            await persistentState.WriteStateAsync();
        }
        catch (Exception ex)
        {
            persistentState.State.Connections[toolName] = new ToolConnection
            {
                ToolName = toolName,
                ToolType = definition.Type,
                Status = ToolConnectionStatus.Error,
                Endpoint = ToolSpecMapper.ResolveEndpoint(definition),
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
            await persistentState.WriteStateAsync();
            throw;
        }
    }

    private bool IsToolAllowed(string agentName, string toolName)
    {
        if (!persistentState.State.AgentToolAccess.TryGetValue(agentName, out var allowed))
            return false;

        return allowed.Contains(toolName, StringComparer.Ordinal);
    }

    private async Task<ToolDefinition> ResolveSecretsAsync(ToolDefinition definition)
    {
        var proxy = grainFactory.GetGrain<ISecretProxyGrain>(_workspaceId);
        var secretToken = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = _workspaceId,
            IssuedTo = $"{_workspaceId}/tool-registry",
            Grants = ["secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var mcpEnv = definition.Mcp is null
            ? null
            : await ResolveSecretsAsync(definition.Mcp.Env, proxy, secretToken);
        var authToken = definition.OpenApi?.Auth?.Token;
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            foreach (var secretPath in SecretPlaceholderParser.EnumeratePaths(authToken))
            {
                await proxy.RegisterSecretAsync(secretPath, secretToken);
            }

            authToken = await proxy.SubstituteAsync(authToken);
        }

        return definition with
        {
            Mcp = definition.Mcp is null ? null : definition.Mcp with { Env = mcpEnv ?? [] },
            OpenApi = definition.OpenApi is null
                ? null
                : definition.OpenApi with
                {
                    Auth = definition.OpenApi.Auth is null
                        ? null
                        : definition.OpenApi.Auth with { Token = authToken }
                }
        };
    }

    private static async Task<Dictionary<string, string>> ResolveSecretsAsync(
        Dictionary<string, string> source,
        ISecretProxyGrain proxy,
        CapabilityToken token)
    {
        var result = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var resolvedValue = value;
            foreach (var secretPath in SecretPlaceholderParser.EnumeratePaths(value))
            {
                await proxy.RegisterSecretAsync(secretPath, token);
            }

            resolvedValue = await proxy.SubstituteAsync(value);
            result[key] = resolvedValue;
        }

        return result;
    }

    private void EnsureWorkspaceId()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceId))
            return;

        try
        {
            _workspaceId = this.GetPrimaryKeyString();
        }
        catch (NullReferenceException)
        {
            _workspaceId = string.IsNullOrWhiteSpace(persistentState.State.WorkspaceId)
                ? "unknown-workspace"
                : persistentState.State.WorkspaceId;
        }
    }
}
