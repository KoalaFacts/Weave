using Microsoft.Extensions.Logging;
using Orleans;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public sealed class AgentSupervisorGrain(
    IGrainFactory grainFactory,
    ILogger<AgentSupervisorGrain> logger) : Grain, IAgentSupervisorGrain
{
    private readonly List<string> _agentNames = [];
    private string _workspaceId = null!;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _workspaceId = this.GetPrimaryKeyString();
        return Task.CompletedTask;
    }

    public async Task ActivateAllAsync(WorkspaceManifest manifest)
    {
        logger.LogInformation("Activating {Count} agents for workspace {WorkspaceId}",
            manifest.Agents.Count, _workspaceId);

        _agentNames.Clear();

        foreach (var (agentName, definition) in manifest.Agents)
        {
            var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{_workspaceId}/{agentName}");

            try
            {
                await agentGrain.ActivateAgentAsync(WorkspaceId.From(_workspaceId), definition);
                _agentNames.Add(agentName);

                foreach (var toolName in definition.Tools)
                {
                    var toolGrain = grainFactory.GetGrain<IToolRegistryGrain>(_workspaceId);
                    var connection = await toolGrain.GetConnectionAsync(toolName);
                    if (connection is { Status: ToolConnectionStatus.Connected })
                    {
                        await agentGrain.ConnectToolAsync(toolName);
                    }
                }

                logger.LogInformation("Agent {AgentName} activated in workspace {WorkspaceId}",
                    agentName, _workspaceId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to activate agent {AgentName} in workspace {WorkspaceId}",
                    agentName, _workspaceId);
                throw;
            }
        }
    }

    public async Task DeactivateAllAsync()
    {
        logger.LogInformation("Deactivating all agents for workspace {WorkspaceId}", _workspaceId);

        foreach (var agentName in _agentNames)
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

        _agentNames.Clear();
    }

    public async Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync()
    {
        var states = new List<AgentState>();
        foreach (var agentName in _agentNames)
        {
            var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{_workspaceId}/{agentName}");
            states.Add(await agentGrain.GetStateAsync());
        }
        return states;
    }

    public async Task<AgentState?> GetAgentStateAsync(string agentName)
    {
        if (!_agentNames.Contains(agentName))
            return null;

        var agentGrain = grainFactory.GetGrain<IAgentGrain>($"{_workspaceId}/{agentName}");
        return await agentGrain.GetStateAsync();
    }
}
