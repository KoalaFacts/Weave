namespace Weave.Agents.Heartbeat;

/// <summary>
/// Actor that manages proactive behavior for an agent.
/// Keyed by {workspaceId}/{agentName}.
/// Wakes the agent periodically to check its task list and act.
/// </summary>
public interface IHeartbeatActor
{
    Task StartAsync(HeartbeatConfig config);
    Task StopAsync();
    Task<HeartbeatState> GetStateAsync();
}
