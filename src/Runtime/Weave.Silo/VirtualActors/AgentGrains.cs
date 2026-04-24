using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Shared.VirtualActors;
using Weave.Workspaces.Models;

namespace Weave.Silo.VirtualActors;

public sealed class AgentActorGrain : Grain, IAgentActorGrain
{
    private readonly AgentActor _actor;

    public AgentActorGrain(
        IVirtualActorProvider actors,
        IAgentChatPipeline chatPipeline,
        ILifecycleManager lifecycleManager,
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<AgentActor> logger,
        [PersistentState("agent", "Default")] IPersistentState<AgentState> state)
    {
        _actor = new AgentActor(actors, chatPipeline, lifecycleManager, eventBus, timeProvider, logger,
            new OrleansActorState<AgentState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition) =>
        _actor.ActivateAgentAsync(workspaceId, definition);

    public Task DeactivateAsync() => _actor.DeactivateAsync();
    public Task<AgentState> GetStateAsync() => _actor.GetStateAsync();
    public Task<AgentChatResponse> SendAsync(AgentMessage message) => _actor.SendAsync(message);
    public Task<AgentTaskInfo> SubmitTaskAsync(string description) => _actor.SubmitTaskAsync(description);

    public Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof) =>
        _actor.CompleteTaskAsync(taskId, success, proof);

    public Task ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback = null, VerificationRecord? verification = null) =>
        _actor.ReviewTaskAsync(taskId, accepted, feedback, verification);

    public Task ConnectToolAsync(string toolName) => _actor.ConnectToolAsync(toolName);
    public Task DisconnectToolAsync(string toolName) => _actor.DisconnectToolAsync(toolName);
}

public sealed class AgentSupervisorActorGrain : Grain, IAgentSupervisorActorGrain
{
    private readonly AgentSupervisorActor _actor;

    public AgentSupervisorActorGrain(
        IVirtualActorProvider actors,
        ILogger<AgentSupervisorActor> logger,
        [PersistentState("agent-supervisor", "Default")] IPersistentState<AgentSupervisorState> state)
    {
        _actor = new AgentSupervisorActor(actors, logger,
            new OrleansActorState<AgentSupervisorState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task ActivateAllAsync(WorkspaceManifest manifest) => _actor.ActivateAllAsync(manifest);
    public Task DeactivateAllAsync() => _actor.DeactivateAllAsync();
    public Task<IReadOnlyList<AgentState>> GetAllAgentStatesAsync() => _actor.GetAllAgentStatesAsync();
    public Task<AgentState?> GetAgentStateAsync(string agentName) => _actor.GetAgentStateAsync(agentName);
}

public sealed class ChannelGatewayActorGrain : Grain, IChannelGatewayActorGrain
{
    private readonly ChannelGatewayActor _actor;

    public ChannelGatewayActorGrain(
        IVirtualActorProvider actors,
        IEventBus eventBus,
        ILogger<ChannelGatewayActor> logger,
        [PersistentState("channel-gateway", "Default")] IPersistentState<ChannelGatewayState> state)
    {
        _actor = new ChannelGatewayActor(actors, eventBus, logger,
            new OrleansActorState<ChannelGatewayState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RegisterChannelAsync(ChannelConfig config) => _actor.RegisterChannelAsync(config);
    public Task UnregisterChannelAsync(ChannelId channelId) => _actor.UnregisterChannelAsync(channelId);

    public Task<OutboundMessage> RouteInboundAsync(InboundMessage message) =>
        _actor.RouteInboundAsync(message);

    public Task<IReadOnlyList<ChannelConfig>> GetChannelsAsync() => _actor.GetChannelsAsync();
    public Task SetRoutingRuleAsync(string pattern, string agentName) => _actor.SetRoutingRuleAsync(pattern, agentName);
}

#pragma warning disable CA1001 // Disposal handled in OnDeactivateAsync
public sealed class HeartbeatActorGrain : Grain, IHeartbeatActorGrain
#pragma warning restore CA1001
{
    private readonly HeartbeatActor _actor;

    public HeartbeatActorGrain(
        IVirtualActorProvider actors,
        TimeProvider timeProvider,
        ILogger<HeartbeatActor> logger)
    {
        _actor = new HeartbeatActor(actors, new OrleansActorTimerRegistry(this), timeProvider, logger);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _actor.Dispose();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task StartAsync(Weave.Agents.Heartbeat.HeartbeatConfig config) => _actor.StartAsync(config);
    public Task StopAsync() => _actor.StopAsync();
    public Task<HeartbeatState> GetStateAsync() => _actor.GetStateAsync();
}

public sealed class ProofValidatorActorGrain : Grain, IProofValidatorActorGrain
{
    private readonly ProofValidatorActor _actor;

    public ProofValidatorActorGrain(
        IAgentChatClientFactory chatClientFactory,
        ILogger<ProofValidatorActor> logger)
    {
        _actor = new ProofValidatorActor(chatClientFactory, logger);
    }

    public Task<VerificationVote> ValidateAsync(string validatorId, ProofOfWork proof, List<VerificationCondition> conditions, string? modelId = null) =>
        _actor.ValidateAsync(validatorId, proof, conditions, modelId);
}

public sealed class ProofVerifierActorGrain : Grain, IProofVerifierActorGrain
{
    private readonly ProofVerifierActor _actor;

    public ProofVerifierActorGrain(
        IVirtualActorProvider actors,
        IEventBus eventBus,
        ILogger<ProofVerifierActor> logger,
        [PersistentState("verifier", "Default")] IPersistentState<VerifierState> state)
    {
        _actor = new ProofVerifierActor(actors, eventBus, logger,
            new OrleansActorState<VerifierState>(state));
    }

    public Task VerifyAsync(WorkspaceId workspaceId, string agentName, AgentTaskId taskId, ProofOfWork proof) =>
        _actor.VerifyAsync(workspaceId, agentName, taskId, proof);

    public Task ConfigureAsync(List<VerificationCondition> conditions, int requiredValidators, List<ValidatorConfig>? validatorConfigs = null) =>
        _actor.ConfigureAsync(conditions, requiredValidators, validatorConfigs);

    public Task<List<VerificationCondition>> GetConditionsAsync() => _actor.GetConditionsAsync();
}

public sealed class SkillMemoryActorGrain : Grain, ISkillMemoryActorGrain
{
    private readonly SkillMemoryActor _actor;

    public SkillMemoryActorGrain(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<SkillMemoryActor> logger,
        [PersistentState("skill-memory", "Default")] IPersistentState<SkillMemoryState> state)
    {
        _actor = new SkillMemoryActor(eventBus, timeProvider, logger,
            new OrleansActorState<SkillMemoryState>(state));
    }

    public Task<SkillDocument> StoreSkillAsync(SkillDocument skill) => _actor.StoreSkillAsync(skill);
    public Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int maxResults = 5) => _actor.SearchAsync(query, maxResults);
    public Task<SkillDocument?> GetSkillAsync(SkillId skillId) => _actor.GetSkillAsync(skillId);
    public Task RecordUsageAsync(SkillId skillId, bool success) => _actor.RecordUsageAsync(skillId, success);
    public Task<IReadOnlyList<SkillDocument>> GetAllSkillsAsync() => _actor.GetAllSkillsAsync();
    public Task RemoveSkillAsync(SkillId skillId) => _actor.RemoveSkillAsync(skillId);
}

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

public sealed class UserModelActorGrain : Grain, IUserModelActorGrain
{
    private readonly UserModelActor _actor;

    public UserModelActorGrain(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<UserModelActor> logger,
        [PersistentState("user-model", "Default")] IPersistentState<UserProfileState> state)
    {
        _actor = new UserModelActor(eventBus, timeProvider, logger,
            new OrleansActorState<UserProfileState>(state));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        _actor.OnActivatedAsync(this.GetPrimaryKeyString(), cancellationToken);

    public Task RecordInteractionAsync(InteractionRecord record) => _actor.RecordInteractionAsync(record);
    public Task SetPreferenceAsync(string key, string value) => _actor.SetPreferenceAsync(key, value);
    public Task SetDomainContextAsync(string key, string value) => _actor.SetDomainContextAsync(key, value);
    public Task<UserProfileState> GetProfileAsync() => _actor.GetProfileAsync();
    public Task<string> GetContextSummaryAsync() => _actor.GetContextSummaryAsync();
    public Task ClearAsync() => _actor.ClearAsync();
}
