using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class AgentSupervisorGrain(
    IGrainFactory grainFactory,
    ILogger<AgentSupervisorGrain> logger,
    [PersistentState("agent-supervisor", "Default")] IPersistentState<AgentSupervisorState> persistentState) : Grain, IAgentSupervisorGrain
{
    private string _workspaceId = string.Empty;

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

    public async Task ActivateAllAsync(WorkspaceManifest manifest)
    {
        EnsureWorkspaceId();
        logger.LogInformation("Activating {Count} agents for workspace {WorkspaceId}", manifest.Agents.Count, _workspaceId);

        persistentState.State.AgentNames.Clear();

        foreach (var (agentName, definition) in manifest.Agents)
        {
            var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{_workspaceId}/{agentName}");

            try
            {
                await agentGrain.ActivateAgentAsync(WorkspaceId.From(_workspaceId), definition);
                persistentState.State.AgentNames.Add(agentName);

                foreach (var toolName in definition.Tools)
                {
                    var toolGrain = grainFactory.GetGrain<IToolRegistryGrain>(_workspaceId);
                    var connection = await toolGrain.GetConnectionAsync(toolName);
                    if (connection is { Status: ToolConnectionStatus.Connected })
                        await agentGrain.ConnectToolAsync(toolName);
                }

                logger.LogInformation("Agent {AgentName} activated in workspace {WorkspaceId}", agentName, _workspaceId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to activate agent {AgentName} in workspace {WorkspaceId}", agentName, _workspaceId);
                throw;
            }
        }

        await persistentState.WriteStateAsync();
    }

    public async Task DeactivateAllAsync()
    {
        EnsureWorkspaceId();
        logger.LogInformation("Deactivating all agents for workspace {WorkspaceId}", _workspaceId);

        foreach (var agentName in persistentState.State.AgentNames)
        {
            var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{_workspaceId}/{agentName}");
            try
            {
                await agentGrain.DeactivateAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deactivate agent {AgentName}", agentName);
            }
        }

        persistentState.State.AgentNames.Clear();
        await persistentState.WriteStateAsync();
    }

    public async Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync()
    {
        EnsureWorkspaceId();
        var states = new List<AgentState>(persistentState.State.AgentNames.Count);
        foreach (var agentName in persistentState.State.AgentNames)
        {
            var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{_workspaceId}/{agentName}");
            states.Add(await agentGrain.GetStateAsync());
        }

        return states;
    }

    public async Task<AgentState?> GetAgentStateAsync(string agentName)
    {
        EnsureWorkspaceId();
        if (!persistentState.State.AgentNames.Contains(agentName, StringComparer.Ordinal))
            return null;

        var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{_workspaceId}/{agentName}");
        return await agentGrain.GetStateAsync();
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
