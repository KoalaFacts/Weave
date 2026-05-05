using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Manifest;
namespace Weave.Silo.VirtualActors;

public sealed class ToolRegistryActorGrain : Grain, IToolRegistryActorGrain
{
    private readonly ToolRegistryActor _actor;

    public ToolRegistryActorGrain(
        IVirtualActorProvider actors,
        ICapabilityTokenService tokenService,
        ILifecycleManager lifecycleManager,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<ToolRegistryActor> logger,
        [PersistentState("tool-registry", "Default")] IPersistentState<ToolRegistryState> state)
    {
        _actor = new ToolRegistryActor(actors, tokenService, lifecycleManager, eventBus, timeProvider, logger,
            new OrleansActorState<ToolRegistryState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task ConnectToolsAsync(Dictionary<string, ToolDefinition> tools) => _actor.ConnectToolsAsync(tools);
    public Task ConfigureAccessAsync(Dictionary<string, List<string>> agentToolAccess) => _actor.ConfigureAccessAsync(agentToolAccess);
    public Task GrantAgentToolsAsync(string agentName, IReadOnlyList<string> toolNames) => _actor.GrantAgentToolsAsync(agentName, toolNames);
    public Task DisconnectAllAsync() => _actor.DisconnectAllAsync();
    public Task<ToolConnection?> GetConnectionAsync(string toolName) => _actor.GetConnectionAsync(toolName);
    public Task<IReadOnlyList<ToolConnection>> GetAllConnectionsAsync() => _actor.GetAllConnectionsAsync();
    public Task<ToolResolution?> ResolveAsync(string agentName, string toolName) => _actor.ResolveAsync(agentName, toolName);
}
