using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Actors;
using Weave.Tools.Mapping;
using Weave.Workspaces.Models;

namespace Weave.Agents.Actors;

public sealed class ToolRegistryActor(
    IVirtualActorProvider actors,
    ICapabilityTokenService tokenService,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<ToolRegistryActor> logger,
    IActorState<ToolRegistryState> persistentState) : IToolRegistryActor
{
    private readonly ToolRegistryConnector _connector = new(
        actors,
        tokenService,
        lifecycleManager,
        eventBus,
        timeProvider,
        new ToolSecretResolver(actors, tokenService),
        logger,
        persistentState);

    private string _workspaceId = "unset";

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        await persistentState.ReadStateAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(key))
            _workspaceId = key;
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
            await ConnectOneAsync(toolName, definition);
        }

        await persistentState.WriteStateAsync();
    }

    private async Task ConnectOneAsync(string toolName, ToolDefinition definition)
    {
        try
        {
            await ConnectOneAsync(toolName, definition);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException)
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
        persistentState.State.AgentToolAccess[agentName] = toolNames is null
            ? []
            : [.. toolNames.Distinct(StringComparer.Ordinal)];
        await persistentState.WriteStateAsync();
    }

    public async Task DisconnectAllAsync()
    {
        EnsureWorkspaceId();
        await _connector.DisconnectAllAsync(_workspaceId);
    }

    public Task<ToolConnection?> GetConnectionAsync(string toolName)
    {
        persistentState.State.Connections.TryGetValue(toolName, out var connection);
        return Task.FromResult(connection);
    }

    public Task<IReadOnlyList<ToolConnection>> GetAllConnectionsAsync()
    {
        // See CapabilityTemplateActor — assign to List<T> so Orleans
        // doesn't trip on the synthesized <>z__ReadOnlyArray type.
        List<ToolConnection> result = [.. persistentState.State.Connections.Values];
        return Task.FromResult<IReadOnlyList<ToolConnection>>(result);
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
            await ConnectOneAsync(toolName, definition);
            connection = persistentState.State.Connections[toolName];
        }

        var actorKey = $"{_workspaceId}/{toolName}";
        var toolActor = actors.GetActor<IToolActor>(VirtualActorId.From(actorKey));
        if (await toolActor.GetHandleAsync() is null)
        {
            await ConnectOneAsync(toolName, definition);
        }

        var token = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = _workspaceId,
            IssuedTo = $"{_workspaceId}/{agentName}",
            Grants = [$"tool:{toolName}"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var schema = await toolActor.GetSchemaAsync();
        return new ToolResolution
        {
            ToolName = toolName,
            ActorKey = actorKey,
            Token = token,
            Schema = schema
        };
    }

    private bool IsToolAllowed(string agentName, string toolName)
    {
        if (!persistentState.State.AgentToolAccess.TryGetValue(agentName, out var allowed))
            return false;

        return allowed.Contains(toolName, StringComparer.Ordinal);
    }

    private void EnsureWorkspaceId()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceId) && !string.Equals(_workspaceId, "unset", StringComparison.Ordinal))
            return;

        _workspaceId = string.IsNullOrWhiteSpace(persistentState.State.WorkspaceId)
            ? "unknown-workspace"
            : persistentState.State.WorkspaceId;
    }
}
