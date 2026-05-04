using System.Collections.Frozen;
using Weave.Agents.Actors;
using Weave.Agents.Heartbeat;
using Weave.Security.Actors;
using Weave.Tools.Actors;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.VirtualActors;

public sealed class OrleansVirtualActorProvider(IActorFactory actorFactory) : IVirtualActorProvider
{
    private static readonly FrozenDictionary<Type, Type> GrainMap = new Dictionary<Type, Type>
    {
        [typeof(IAgentActor)] = typeof(IAgentActorGrain),
        [typeof(IAgentSupervisorActor)] = typeof(IAgentSupervisorActorGrain),
        [typeof(IChannelGatewayActor)] = typeof(IChannelGatewayActorGrain),
        [typeof(IEpisodicMemoryActor)] = typeof(IEpisodicMemoryActorGrain),
        [typeof(IHeartbeatActor)] = typeof(IHeartbeatActorGrain),
        [typeof(IProofValidatorActor)] = typeof(IProofValidatorActorGrain),
        [typeof(IProofVerifierActor)] = typeof(IProofVerifierActorGrain),
        [typeof(ISkillMemoryActor)] = typeof(ISkillMemoryActorGrain),
        [typeof(IToolRegistryActor)] = typeof(IToolRegistryActorGrain),
        [typeof(IUserModelActor)] = typeof(IUserModelActorGrain),
        [typeof(IToolActor)] = typeof(IToolActorGrain),
        [typeof(IMarketplaceActor)] = typeof(IMarketplaceActorGrain),
        [typeof(ISecretProxyActor)] = typeof(ISecretProxyActorGrain),
        [typeof(IWorkspaceActor)] = typeof(IWorkspaceActorGrain),
        [typeof(IWorkspaceRegistryActor)] = typeof(IWorkspaceRegistryActorGrain),
        [typeof(ICapabilityTemplateActor)] = typeof(ICapabilityTemplateActorGrain),
    }.ToFrozenDictionary();

    public TActor GetActor<TActor>(VirtualActorId id)
        where TActor : class
    {
        var grainType = GrainMap.GetValueOrDefault(typeof(TActor)) ?? typeof(TActor);
        return (TActor)actorFactory.GetGrain(grainType, id.Value);
    }
}
